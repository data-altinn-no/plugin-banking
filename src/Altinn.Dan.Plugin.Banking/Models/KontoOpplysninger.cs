
using FileHelpers;

public class KontoOpplysninger
{
    public Endpoint[] endpoints { get; set; }
    public int total { get; set; }
}

[DelimitedRecord(",")]
public class Endpoint
{
    public string orgNo { get; set; }
    public string serviceType { get; set; }
    public string url { get; set; }
    public string transportProfile { get; set; }
    public string environment { get; set; }
}
