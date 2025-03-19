using System;

namespace Altinn.Dan.Plugin.Banking.Models
{
    public static class PluginConstants
    {
        private const string LEGAL_MANDATE = "Arveloven § 92 første ledd og § 118, jf. § 88 a, jf. forskrift om Digitalt dødsbo";

        public static string LegalMandate => Uri.EscapeDataString(LEGAL_MANDATE);
    }
}
