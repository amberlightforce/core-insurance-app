namespace CoreIns.IntegrationTests.GreekSearch;

/// <summary>
/// Synthetic corpus of Greek and Latin names (accents, dialytika, final sigma, polytonic forms, capitals, digraphs,
/// punctuation, Latin diacritics, Cyrillic) for the search-key parity test. No real persons are referenced.
/// </summary>
internal static class NameCorpus
{
    private static readonly string[] GreekGivenNames =
    [
        "Γεώργιος", "Γιώργος", "Ιωάννης", "Γιάννης", "Κωνσταντίνος", "Δημήτριος", "Νικόλαος", "Παναγιώτης", "Βασίλειος",
        "Χρήστος", "Αθανάσιος", "Μιχαήλ", "Ευάγγελος", "Σπυρίδων", "Αντώνιος", "Αναστάσιος", "Θεόδωρος", "Ανδρέας",
        "Χαράλαμπος", "Παύλος", "Ηλίας", "Εμμανουήλ", "Σταύρος", "Αλέξανδρος", "Ευθύμιος", "Στυλιανός", "Θωμάς",
        "Άγγελος", "Μάριος", "Ξενοφών", "Ψάλτης", "Ζήσης", "Μαρία", "Ελένη", "Αικατερίνη", "Βασιλική", "Σοφία",
        "Αγγελική", "Γεωργία", "Δήμητρα", "Ευαγγελία", "Κωνσταντίνα", "Ειρήνη", "Παναγιώτα", "Χριστίνα", "Αναστασία",
        "Ευτυχία", "Θεοδώρα", "Ιφιγένεια", "Χαρίκλεια", "Ζωή", "Αλεξάνδρα", "Ευφροσύνη", "Ναυσικά", "Αϊσέ", "Ραΐσα",
        "Μαΐου", "Ἀθηνᾶ", "Ὠκεανός", "ᾨδή",
    ];

    private static readonly string[] GreekFamilyNames =
    [
        "Παπαδόπουλος", "Παπαδοπούλου", "Παπαδάκης", "Γεωργίου", "Ευθυμίου", "Αυγερινός", "Αγγελόπουλος", "Μπακογιάννης",
        "Καμπάνης", "Ντόκος", "Χατζηδάκης", "Ψαρρός", "Σωτηρόπουλος", "Ιωαννίδης", "Νικολάου", "Κωνσταντινίδης",
        "Δημητρίου", "Βασιλείου", "Αθανασίου", "Μιχαηλίδης", "Οικονόμου", "Μακρής", "Παπαγεωργίου", "Αντωνίου",
        "Χριστοδούλου", "Καραγιάννης", "Θεοδωρίδης", "Ζαφειρίου", "Ξυδάκης", "Λαμπράκης", "Μαυρίδης", "Τσακίρης",
        "Φραγκιαδάκης", "Γκίκας", "Ντελής", "Μπαλτάς", "Τζαννετάκης", "Κουτσογιάννης", "Ρήγας", "Σταυρόπουλος",
        "Ευαγγέλου", "Αυγουστίνος", "Ευγενίδης", "Ναυπλιώτης", "Ρολόι", "Πρωτεΐνη", "Αϋπνίας", "Κεχαγιάς", "Γιαννόπουλος",
        "Κυριακίδης", "Χαλκιάς", "Ψυχογιός", "Βλάχος", "Δαμιανός", "Ηλιόπουλος", "Θεοχάρης", "Ιατρίδης", "Λιάπης",
        "Μουρατίδης", "Ξανθόπουλος", "Πετρόπουλος", "Ρουσσάκης", "Συμεωνίδης", "Τριανταφύλλου", "Υφαντής", "Φωτίου",
        "Χαραλαμπίδης", "Ψωμιάδης", "Ωραιόπουλος", "Αλεξίου", "Μπουμπουλίνα", "Γκούμας", "Παπα-Γεωργίου", "Οδός",
        "ΠΑΠΑΔΟΠΟΥΛΟΣ", "ΣΩΤΗΡΌΠΟΥΛΟΣ", "ΕΥΘΥΜΙΟΥ", "παπαδόπουλος", "σωτηροπουλοσ",
    ];

    private static readonly string[] LatinNames =
    [
        "Georgios Papadopoulos", "Yiorgos Papadopoulos", "Giorgos Papadopoulos", "Efthymiou Angeliki", "Dokos",
        "Ntokos", "Hristos", "Christos", "Müller", "Ñúñez", "Gonçalves", "Søren Kierkegaard", "Łukasz Żółć",
        "José María Aznar-López", "O'Brien", "D'Angelo", "Ægir", "Straße", "Ångström", "Dvořák", "Đorđević",
        "François Hollande", "Zoë Saldaña", "Björk Guðmundsdóttir", "İsmail Kılıç", "Hồ Chí Minh", "Ōsaka",
        "Ğlu Şahin", "Mc-Donald", "van der Berg", "de la Cruz", "St. John-Smith", "ALL CAPS NAME", "mixed CASE name",
        "Ivan Petrov", "Иван Петров", "Ёлка Йошкар", "Αννα-Maria Smith", "Smith & Sons Ltd.", "ACME (Hellas) S.A.",
    ];

    private static readonly string[] Special =
    [
        "Λεωφ. Κηφισίας 124, 11526 Αθήνα", "Βασ. Σοφίας 12, 106 74 Αθήνα", "  Παπαδόπουλος   Γεώργιος  ",
        "Παπαδόπουλος\tΓεώργιος", "Μάιος", "ρολόι", "πρωτεΐνη", "αϋπνία", "ευρώ", "Ασφαλιστήριο", "ᾠδή", "άυλος",
        "παίζω", "αύριο", "ΑΣΦ-2", "ΑΣΦ-10", "123456783", "EL123456783", "ικχ-1234", "ΙΚΧ1234",
        "ς", "σ", "Σ", "ΐ", "ΰ", "ϊ", "ϋ", "Ϊ", "Ϋ", "—", "", " ", "Ⓐbc", "Ⅻ Ⅳ", "x²", "１２３",
        "ſ", "ǅ ǈ ǋ", "ﬁ", "µ", "ÿ", "ı", "ϐϑϕϖϰϱϲ", "ﾊﾝｶｸ", "ǰ", "ΐ ΰ",
    ];

    /// <summary>All corpus entries (at least 200).</summary>
    public static IReadOnlyList<string> All { get; } = Build();

    private static List<string> Build()
    {
        var all = new List<string>();
        all.AddRange(GreekGivenNames);
        all.AddRange(GreekFamilyNames);
        all.AddRange(LatinNames);
        all.AddRange(Special);
        for (var index = 0; index < 40; index++)
        {
            all.Add($"{GreekFamilyNames[index]} {GreekGivenNames[index % GreekGivenNames.Length]}");
        }

        return all;
    }
}
