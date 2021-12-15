using Altinn.Dan.Plugin.Banking.Clients;
using System.Collections.Generic;

namespace Altinn.Dan.Plugin.Banking.Models
{
    public class KARResponse
    {
        public ICollection<CustomerRelation> Banks { get; set; }
    }
}
