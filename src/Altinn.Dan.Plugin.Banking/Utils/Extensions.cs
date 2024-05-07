using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Altinn.Dan.Plugin.Banking.Utils
{
    public static class Extensions
    {
        public static bool AreEqual(this Endpoint ep, string? orgno)
        {
            if (ep == null || string.IsNullOrEmpty(orgno))
                return false;

            return ep.orgNo == orgno;
        }
    }
}
