namespace Cotacoes.Web.Models;

public sealed class SupabaseOptions
{
    public string Url { get; set; } = "";
    public string PublishableKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string ConnectionString { get; set; } = "";

    public bool BancoConfigurado => !string.IsNullOrWhiteSpace(ConnectionString);
    public bool AuthConfigurado => BancoConfigurado
        && !string.IsNullOrWhiteSpace(Url)
        && !string.IsNullOrWhiteSpace(PublishableKey)
        && !string.IsNullOrWhiteSpace(SecretKey);
}
