using System.Globalization;
using System.Text.Json;

namespace Cotacoes.Web.Services;

public sealed record CotacaoCambio(decimal ValorVenda, DateOnly DataReferencia);

public sealed class CambioService(IHttpClientFactory httpClientFactory, ILogger<CambioService> logger)
{
    private const string BaseUrl = "https://olinda.bcb.gov.br/olinda/servico/PTAX/versao/v1/odata";

    public async Task<CotacaoCambio?> ObterUltimaPtaxVendaAsync(CancellationToken cancellationToken = default)
    {
        var fim = DateTime.Today;
        var inicio = fim.AddDays(-10);
        var inicioTexto = inicio.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
        var fimTexto = fim.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
        var url = $"{BaseUrl}/CotacaoDolarPeriodo(dataInicial=@dataInicial,dataFinalCotacao=@dataFinalCotacao)" +
                  $"?@dataInicial='{inicioTexto}'&@dataFinalCotacao='{fimTexto}'&$top=1" +
                  "&$orderby=dataHoraCotacao%20desc&$format=json";

        try
        {
            using var cliente = httpClientFactory.CreateClient();
            cliente.Timeout = TimeSpan.FromSeconds(12);
            using var resposta = await cliente.GetAsync(url, cancellationToken);
            resposta.EnsureSuccessStatusCode();
            await using var conteudo = await resposta.Content.ReadAsStreamAsync(cancellationToken);
            using var documento = await JsonDocument.ParseAsync(conteudo, cancellationToken: cancellationToken);
            var registros = documento.RootElement.GetProperty("value");
            if (registros.GetArrayLength() == 0) return null;

            var registro = registros[0];
            var valor = registro.GetProperty("cotacaoVenda").GetDecimal();
            var dataTexto = registro.GetProperty("dataHoraCotacao").GetString();
            if (valor <= 0 || !DateTime.TryParse(dataTexto, CultureInfo.InvariantCulture, DateTimeStyles.None, out var data))
                return null;
            return new(valor, DateOnly.FromDateTime(data));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Não foi possível consultar a PTAX do Banco Central.");
            return null;
        }
    }
}
