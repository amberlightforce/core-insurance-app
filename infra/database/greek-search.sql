-- Greek search and collation objects (PLAN §4.6; REQ-MKT-178, REQ-MKT-340, REQ-PTY-065/066, NFR-PTY-013).
-- Idempotent: safe to run on every deployment. Run by the migrate job after bootstrap.sql (which creates the
-- unaccent and pg_trgm extensions). Requires PostgreSQL 17 (builtin collation pg_c_utf8) built with ICU, and a UTF8
-- database.
--
-- What lives here and why:
--   * coreins_search_key(text)  - the database twin of GreekSearchNormalizer.SearchKey (CoreIns.CountryPacks.GR).
--     The application computes and stores search keys through the MKT language rules; this function exists so that
--     SQL (expression indexes, data fixes, migration checks) produces byte-identical keys. A parity test keeps both
--     equal over a corpus of Greek and Latin names. It is Unicode-generic (NFD, combining-mark removal, simple upper
--     case, punctuation collapse) - transliteration and Greeklish variants are NOT done in SQL: they come only from
--     NameTransliterator.searchVariants (REQ-MKT-178), computed by the application.
--   * el_gr_ci_ai                - ICU non-deterministic collation for Greek sorting and accent/case-insensitive
--     equality (UCA primary strength, numeric digit ordering), matching GreekLanguageRules.SortComparer.
--   * greek_unaccent_v1          - text-search configuration (unaccent + simple, no stemming: names must not stem).
--     The name is versioned: the IF NOT EXISTS guard never alters an existing configuration, so a changed mapping
--     must ship as greek_unaccent_v2 (and coreins_search_tsvector repointed, then indexes rebuilt). unaccent is kept so
--     the configuration is also correct on raw text; over coreins_search_key input it is a no-op.
--   * coreins_search_tsvector(text) - tsvector over the search key, so case folding does not depend on the
--     database's LC_CTYPE.
--
-- Operations: expression indexes over these IMMUTABLE functions and columns sorted by el_gr_ci_ai depend on the
-- PostgreSQL Unicode tables (normalize, upper under pg_c_utf8) and on the ICU version. After a PostgreSQL major
-- upgrade, an ICU library change (ALTER COLLATION ... REFRESH VERSION warns) or any change to this file, REINDEX the
-- indexes that use them (see infra/README.md).

-- 1. Search key -----------------------------------------------------------------------------------------------------
-- Steps (identical to the C# normaliser):
--   NFD -> remove combining marks U+0300-036F, U+1AB0-1AFF, U+1DC0-1DFF, U+20D0-20FF, U+FE20-FE2F -> NFC
--   -> simple Unicode upper case (pg_c_utf8: sigma and final sigma both become capital sigma)
--   -> runs of characters other than [:alnum:] (Unicode Alphabetic or ASCII digit under pg_c_utf8) become one space
--   -> trim.
CREATE OR REPLACE FUNCTION public.coreins_search_key(input text)
  RETURNS text
  LANGUAGE sql
  IMMUTABLE STRICT PARALLEL SAFE
RETURN btrim(
  regexp_replace(
    upper(
      normalize(
        regexp_replace(normalize(input, NFD), '[̀-ͯ᪰-᫿᷀-᷿⃐-⃿︠-︯]+', '', 'g'),
        NFC
      ) COLLATE pg_catalog.pg_c_utf8
    ),
    '[^[:alnum:]]+', ' ', 'g'
  ),
  ' '
);

COMMENT ON FUNCTION public.coreins_search_key(text) IS
  'Accent-, case- and final-sigma-insensitive search key; twin of GreekSearchNormalizer.SearchKey (REQ-MKT-178, REQ-PTY-065).';

-- 2. Greek collation ------------------------------------------------------------------------------------------------
-- Greek locale, primary strength (ignores case, tonos, dialytika and final-sigma form), numeric ordering of digit runs.
-- Non-deterministic: usable for ORDER BY and '=' (not for LIKE in PostgreSQL 17).
DO $collation$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_collation
    WHERE collname = 'el_gr_ci_ai' AND collnamespace = 'public'::regnamespace
  ) THEN
    CREATE COLLATION public.el_gr_ci_ai (provider = icu, locale = 'el-GR-u-kn-ks-level1', deterministic = false);
  END IF;
END
$collation$;

COMMENT ON COLLATION public.el_gr_ci_ai IS
  'Greek sorting and equality ignoring case and accents, numeric digit order (NFR-PTY-013, DESIGN-B D.3).';

-- 3. Text search configuration ----------------------------------------------------------------------------------------
CREATE EXTENSION IF NOT EXISTS unaccent;

DO $tsconfig$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_ts_config
    WHERE cfgname = 'greek_unaccent_v1' AND cfgnamespace = 'public'::regnamespace
  ) THEN
    CREATE TEXT SEARCH CONFIGURATION public.greek_unaccent_v1 (COPY = pg_catalog.simple);
    ALTER TEXT SEARCH CONFIGURATION public.greek_unaccent_v1
      ALTER MAPPING FOR asciiword, asciihword, hword_asciipart, word, hword, hword_part
      WITH public.unaccent, pg_catalog.simple;
  END IF;
END
$tsconfig$;

-- The search key is computed first, so the tsvector is the same whatever the database LC_CTYPE is.
CREATE OR REPLACE FUNCTION public.coreins_search_tsvector(input text)
  RETURNS tsvector
  LANGUAGE sql
  IMMUTABLE STRICT PARALLEL SAFE
RETURN to_tsvector('public.greek_unaccent_v1'::regconfig, public.coreins_search_key(input));

COMMENT ON FUNCTION public.coreins_search_tsvector(text) IS
  'Full-text vector over coreins_search_key (greek_unaccent_v1 configuration, no stemming).';
