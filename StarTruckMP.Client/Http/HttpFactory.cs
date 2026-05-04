using System.Net.Http;

namespace StarTruckMP.Client.Http;

public static class HttpFactory
{
    public static HttpClient Create()
    {
        if (App.IgnoreSslValidation.Value)
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true,
            };
            return new HttpClient(handler);
        }
        
        return new HttpClient();
    }
}