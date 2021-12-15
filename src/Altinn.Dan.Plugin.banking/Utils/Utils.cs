using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Altinn.Dan.Plugin.Banking.Utils
{
    public static class OEDUtils
    {
        public static string MapSsn(string ssn, string externalSource)
        {
            // todo Make this table driven and more maintainable when we have more interfaces and test users
            // todo Differntiate between different test enviroments. Prod should allways return the input without modification.
            if (externalSource == "svv")
            {
                switch (ssn)
                {
                    case "12113701569":
                    case "02056600860":
                    case "04103300113":
                    case "11093600373":
                    case "10045800887":
                        return ssn;
                    default: return "07047901388";
                }
            }
            else if (externalSource == "kartverket")
            {
                switch (ssn)
                {
                    case "12113701569":
                    case "02056600860":
                    case "04103300113":
                    case "11093600373":
                    case "10045800887":
                        return ssn;
                    case "23076102252": return "02056600860";
                    default: return "12113701569";
                }
            }
            else if (externalSource == "norskpensjon")
            {
                switch (ssn)
                {
                    case "12113701569":
                    case "02056600860":
                    case "04103300113":
                    case "11093600373":
                    case "10045800887":
                        return ssn;
                    default: return "11093600373";
                }
            }
            else if (externalSource.StartsWith("skatt", StringComparison.OrdinalIgnoreCase))
            {
                switch (ssn)
                {
                    default: return "07078600378";
                }
            }
            else if (externalSource == "bank" || externalSource == "kar")
            {
                switch (ssn)
                {
                    case "12113701569":
                    case "02056600860":
                    case "04103300113":
                    case "11093600373":
                    case "10045800887":
                        return ssn;
                    default: return "07056120453";
                }
            }
            else if (externalSource == "ektepaktLosore")
            {
                switch (ssn)
                {
                    default: return "01025901750";
                }
            }

            return ssn;
        }
    }
}
