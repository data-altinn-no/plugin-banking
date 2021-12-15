using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Altinn.Dan.Plugin.banking.Exceptions
{
    public class MissingSettingsException : Exception
    {
        /// <summary>
        /// The constructor
        /// </summary>
        /// <param name="message">The error message</param>
        public MissingSettingsException(string message) : base(message)
        {
        }
    }
}
