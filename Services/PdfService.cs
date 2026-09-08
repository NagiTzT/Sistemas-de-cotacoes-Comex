using Cotacoes.Web.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Cotacoes.Web.Services;

public sealed class PdfService
{
    public byte[] Gerar(Cotacao cotacao) => Document.Create(document =>
    {
        foreach (var grupo in cotacao.Modelos.Chunk(2))
            document.Page(page => MontarPagina(page, cotacao, grupo));
        document.Page(page => MontarInformacoes(page, cotacao));
    }).GeneratePdf();

    private static void MontarPagina(PageDescriptor page, Cotacao cotacao, IEnumerable<ModeloCotacao> modelos)
    {
        var pharma = cotacao.ModeloPdf == ModeloPdf.FullPharma;
        var cor = pharma ? "#433D91" : "#E50070";
        page.Size(PageSizes.A4); page.Margin(22); page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(7));
        page.Header().Column(c =>
        {
            c.Item().Background(cor).Padding(7).AlignCenter().Text(pharma ? "@fullbrandsgroup    fullbrands.com.br    Itajaí/SC" : "@beauty.fullbrands    fullbrands.com.br    Itajaí/SC").FontColor(Colors.White);
            c.Item().PaddingVertical(10).AlignCenter().Text(pharma ? "FULLPHARMA" : "FULLBEAUTY").FontSize(24).Bold().FontColor(cor);
            c.Item().Background(cor).Padding(5).AlignCenter().Text("PROPOSTA COMERCIAL").Bold().FontColor(Colors.White);
        });
        page.Content().PaddingTop(14).Column(c =>
        {
            c.Spacing(10);
            c.Item().Text($"Aos cuidados de {cotacao.Cliente},");
            c.Item().Text(cotacao.DataCotacao.ToDateTime(TimeOnly.MinValue).ToString("dddd, dd 'de' MMMM 'de' yyyy", new System.Globalization.CultureInfo("pt-BR")));
            c.Item().PaddingBottom(14).Text("Detalhes do Produto").Bold();
            var numero = 1;
            foreach (var modelo in modelos.Where(x => x.ItemAdicionado)) { c.Item().Element(x => Modelo(x, cotacao, modelo, numero++, pharma)); }
        });
        page.Footer().AlignCenter().Text(x => { x.Span("Fullbrands • Proposta "); x.CurrentPageNumber(); });
    }

    private static void Modelo(IContainer root, Cotacao cotacao, ModeloCotacao modelo, int numero, bool pharma)
    {
        root.PaddingBottom(18).Column(c =>
        {
            c.Item().Border(1).BorderColor("#B9B9B9").Column(info =>
            {
                info.Item().Background("#D8D8D8").AlignCenter().Text(string.IsNullOrWhiteSpace(modelo.Nome) ? $"MODELO {numero}" : modelo.Nome.ToUpperInvariant()).Bold();
                info.Item().Padding(2).Text($"PRODUTO: {modelo.Item.Descricao}\nCÓDIGO: {modelo.Item.Codigo}\nNCM: {modelo.Item.Ncm}");
                info.Item().Background("#D8D8D8").AlignCenter().Text("PRAZOS CONSIDERADOS").Bold();
                info.Item().Padding(2).Text($"LEAD TIME PRODUÇÃO: {(pharma ? "30 - 60" : "60")} dias\nLEAD TIME TRANSPORTE MARÍTIMO: 60 dias");
            });
            c.Item().PaddingTop(8).Table(t =>
            {
                t.ColumnsDefinition(cols => { for (var i = 0; i < 9; i++) cols.RelativeColumn(i is 3 ? 1.5f : 1); });
                var headers = new List<string> { "Quantidade", "Material", "Tamanho", "Especificação", "Capacidade", "Preço peça sem impostos", "Preço peça com impostos, sem IPI", "Preço peça com todos impostos", "Valor Total do Pedido" };
                t.Header(h => { foreach (var text in headers) h.Cell().Background("#D8D8D8").Border(1).BorderColor("#B9B9B9").Padding(4).AlignCenter().Text(text).Bold(); });
                var semImpostos = cotacao.PrecoUnitarioSemImpostos(modelo.Item);
                var comImpostosSemIpi = cotacao.PrecoUnitarioComImpostosSemIpi(modelo.Item);
                var comTodosImpostos = cotacao.PrecoUnitarioComTodosImpostos(modelo.Item);
                var values = new List<string> { modelo.Item.Quantidade.ToString("N2"), modelo.Item.Material, modelo.Item.Tamanho, modelo.Item.Especificacao, modelo.Item.Capacidade, Dinheiro(semImpostos), Dinheiro(comImpostosSemIpi), Dinheiro(comTodosImpostos), Dinheiro(modelo.Item.Quantidade * comTodosImpostos) };
                foreach (var value in values) t.Cell().MinHeight(42).Border(1).BorderColor("#B9B9B9").Padding(4).AlignMiddle().Text(value);
            });
        });
    }

    private static void MontarInformacoes(PageDescriptor page, Cotacao cotacao)
    {
        var pharma = cotacao.ModeloPdf == ModeloPdf.FullPharma; var cor = pharma ? "#433D91" : "#E50070";
        page.Size(PageSizes.A4); page.Margin(28); page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(9));
        page.Header().Background(cor).Padding(8).AlignCenter().Text(pharma ? "FULLPHARMA" : "FULLBEAUTY").FontSize(22).Bold().FontColor(Colors.White);
        page.Content().PaddingTop(28).Column(c =>
        {
            c.Spacing(14); c.Item().Text("INFORMAÇÕES ADICIONAIS").FontSize(13).Bold().FontColor(cor);
            c.Item().Text("Os valores apresentados já incluem os custos de câmbio e frete internacional, considerando o cenário atual de mercado. Poderão ocorrer ajustes em caso de variações cambiais, fiscais ou logísticas significativas.");
            c.Item().Text($"• Câmbio considerado para elaboração da proposta: R$ {cotacao.Cambio:N2}\n• Lead time conforme indicado em cada modelo.\n• O cronograma de produção será iniciado após a aprovação do projeto e confirmação do pagamento do sinal.");
            c.Item().Text("Prazo de pagamento: 50% no pedido e 50% no desembaraço, ou por negociação via FIDC, conforme análise e aprovação prévia.");
            c.Item().Text("O valor do ICMS e do ICMS-ST, quando aplicável, não está incluído na proposta e será de responsabilidade do cliente, conforme legislação vigente e regime tributário adotado.");
            c.Item().Text("Esta proposta comercial tem validade de 7 (sete) dias corridos a contar da data de emissão.");
            c.Item().PaddingTop(30).Row(r => { r.RelativeItem().Text("Assinatura de aceite: __________________________"); r.RelativeItem().Text("Data: ____/____/________"); });
        });
    }
    private static string Dinheiro(decimal valor) => valor.ToString("C2", new System.Globalization.CultureInfo("pt-BR"));
}
