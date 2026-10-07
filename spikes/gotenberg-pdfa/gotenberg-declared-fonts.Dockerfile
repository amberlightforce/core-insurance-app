# PRODUCTION SKETCH (not built in the spike): a Gotenberg image whose Chromium can only see the
# declared, versioned font set, so a missing glyph can never be silently satisfied by a system
# fallback font (REQ-DOC-149). Chromium then draws .notdef ("tofu"), which the post-render check
# and veraPDF (ISO 19005-3 6.2.11.8) both reject - the pre-render cmap check stays the primary gate.
FROM gotenberg/gotenberg:8.37.0
USER root
RUN rm -rf /usr/share/fonts/* /usr/local/share/fonts/* \
 && mkdir -p /usr/share/fonts/doc-fontset
COPY fonts/*.ttf /usr/share/fonts/doc-fontset/
RUN fc-cache -f && fc-list
USER gotenberg
