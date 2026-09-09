using System.ComponentModel.DataAnnotations;

namespace Cotacoes.Web.Models;

public enum ModeloPdf { FullBeauty, FullPharma }
public enum ModalidadeCarga { Lcl, Fcl }

public sealed class Cotacao
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Numero { get; set; } = "";
    public DateTime? PdfGeradoEmUtc { get; set; }
    public Guid? CotacaoOrigemId { get; set; }
    public string? PrazosPdf { get; set; }
    public string? InformacoesPdf { get; set; }
    public string PrazosPdfEfetivos => PrazosPdf ?? "LEAD TIME PRODUÇÃO: 60 dias\nLEAD TIME TRANSPORTE: 60 dias";
    public string InformacoesPdfEfetivas => InformacoesPdf ?? CriarInformacoesPdfPadrao();

    public string CriarInformacoesPdfPadrao()
    {
        var pt = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        return "Os valores apresentados já incluem os custos de câmbio e frete internacional, considerando o cenário atual de mercado. Ressaltamos que poderão ocorrer ajustes em caso de variações cambiais, fiscais ou logísticas significativas.\n" +
            $"• Câmbio considerado para elaboração da proposta: R$ {Cambio.ToString("N2", pt)}\n" +
            $"• Frete internacional {Calculo.ModalidadeCarga.ToString().ToUpperInvariant()} considerado para elaboração da proposta: USD {Calculo.FreteInternacionalUsd.ToString("N2", pt)}\n\n" +
            "O cronograma de produção será iniciado imediatamente após a aprovação do projeto e confirmação do pagamento do sinal.\n" +
            "Prazo de pagamento 50% no pedido e 50% no desembaraço ou através de negociação via FIDC (Fundo de Investimento em Direitos Creditórios), conforme análise e aprovação prévia.\n\n" +
            "O valor do ICMS e do ICMS-ST, quando aplicável, não está incluído na presente proposta e será de responsabilidade do cliente, conforme legislação vigente e regime tributário adotado.\n\n" +
            "A presente proposta comercial tem validade de 15 (quinze) dias corridos a contar da data de sua emissão. Após esse período, os valores, prazos e demais condições aqui estabelecidas poderão ser revisados mediante nova negociação entre as partes.";
    }
    public Guid? CriadoPorId { get; set; }
    [Required(ErrorMessage = "Informe o cliente.")] public string Cliente { get; set; } = "";
    [Required(ErrorMessage = "Informe o destino.")] public string Destino { get; set; } = "";
    [Required(ErrorMessage = "Informe a origem.")] public string Origem { get; set; } = "";
    public DateOnly DataCotacao { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    [Range(0.01, double.MaxValue, ErrorMessage = "Informe uma taxa de câmbio válida.")] public decimal Cambio { get; set; }
    public DateOnly? DataReferenciaCambio { get; set; }
    public ModeloPdf ModeloPdf { get; set; } = ModeloPdf.FullBeauty;
    public List<ModeloCotacao> Modelos { get; set; } = [new()];
    public decimal DespesasAdicionaisReais { get; set; }
    public decimal MargemPercentual { get; set; }
    public CalculoCotacao Calculo { get; set; } = new();
    public string Status { get; set; } = "Em preenchimento";

    private IEnumerable<ItemCotacao> Itens => Modelos.Where(x => x.ItemAdicionado).Select(x => x.Item);
    public decimal FobTotalUsd => Itens.Sum(ValorFobUsd);
    public decimal FobTotalReais => FobTotalUsd * Cambio;
    public decimal ThcTotalUsd => Calculo.ModalidadeCarga == ModalidadeCarga.Lcl
        ? Calculo.PesoCargaKg * Calculo.ThcUsdPorKg
        : Calculo.QuantidadeContainers * Calculo.ThcUsdPorContainer;
    public decimal ThcTotalReais => ThcTotalUsd * Cambio;
    public decimal FreteInternacionalReais => Calculo.FreteInternacionalUsd * Cambio;
    public decimal SeguroInternacionalReais =>
        (FobTotalReais + FreteInternacionalReais + ThcTotalReais) * Taxa(Calculo.SeguroInternacionalPercentual);
    public decimal ValorCifReais => FobTotalReais + FreteInternacionalReais + ThcTotalReais + SeguroInternacionalReais;
    public decimal TotalDespesasAduaneirasReais => Calculo.DespesasAduaneiras.TotalReais + DespesasAdicionaisReais;
    public decimal TotalTributosImportacaoReais => Itens.Sum(x => CalcularImportacao(x).TotalTributosReais) + Calculo.TaxaSiscomexReais;
    public decimal TotalCreditosImportacaoReais => Itens.Sum(x => CalcularImportacao(x).TotalCreditosReais);
    public decimal ValorNotaFiscalEntradaReais => ValorCifReais + TotalTributosImportacaoReais + TotalDespesasAduaneirasReais;
    public decimal CustoEntradaLiquidoReais => ValorNotaFiscalEntradaReais - TotalCreditosImportacaoReais;
    public decimal CustoOperacionalReais => ValorNotaFiscalEntradaReais *
                                             Taxa(Calculo.CustoOperacionalPercentual + Calculo.CustoLogisticoPercentual);
    public decimal CustoFinanceiroReais => ValorNotaFiscalEntradaReais * Taxa(Calculo.CustoFinanceiroPercentual);
    public decimal CustoTotalReais => CustoEntradaLiquidoReais + CustoOperacionalReais + CustoFinanceiroReais;
    public decimal FreteEntregaClienteReais
    {
        get
        {
            if (Calculo.UsarFreteEntregaManual) return Calculo.FreteEntregaManualReais;
            var baseFrete = Calculo.CubagemM3 * Calculo.ValorFretePorM3Reais;
            var adValorem = PrecoVendaSemFreteSemIpiReais * Taxa(Calculo.AdValoremFretePercentual);
            var subtotal = baseFrete + Calculo.PedagioReais + adValorem;
            return subtotal + subtotal * Taxa(Calculo.IcmsFretePercentual);
        }
    }
    public decimal PrecoVendaSemFreteSemIpiReais => Itens.Sum(item =>
    {
        return PrecoVendaSemFreteItemReais(item);
    });
    public decimal PrecoVendaSemIpiReais => Itens.Sum(x => x.Quantidade * PrecoUnitarioComImpostosSemIpi(x));
    public decimal PrecoVendaSugeridoReais => Itens.Sum(x => x.Quantidade * PrecoUnitarioComTodosImpostos(x));
    public decimal TotalImpostosVendaReais => Math.Max(0, PrecoVendaSugeridoReais - CustoTotalReais - FreteEntregaClienteReais);
    public decimal TotalImpostosReais => TotalTributosImportacaoReais + TotalImpostosVendaReais;
    public decimal CustoComercialTotalReais => CustoTotalReais + FreteEntregaClienteReais;
    public decimal ComissaoOrigemReais => FobTotalReais * Taxa(Calculo.ComissaoOrigemPercentual);
    public decimal RetornoOperacionalReais => CustoOperacionalReais + PrecoVendaSemIpiReais * Taxa(Calculo.MarkupPercentual);
    public decimal RetornoSobreCapitalPercentual => CustoTotalReais <= 0 ? 0 : RetornoOperacionalReais / CustoTotalReais * 100;

    public decimal PrecoUnitarioSemImpostos(ItemCotacao item)
    {
        if (item.Quantidade <= 0) return 0;
        var quantidadeTotal = Itens.Sum(x => x.Quantidade);
        var fretePorUnidade = quantidadeTotal <= 0 ? 0 : FreteEntregaClienteReais / quantidadeTotal;
        var vendaSemFrete = PrecoVendaSemFreteItemReais(item);
        var creditosVenda = vendaSemFrete * Taxa(item.Impostos.TotalVendaSemIpiPercentual);
        return (vendaSemFrete - creditosVenda) / item.Quantidade + fretePorUnidade;
    }

    public decimal PrecoUnitarioComImpostosSemIpi(ItemCotacao item)
    {
        if (item.Quantidade <= 0) return 0;
        var quantidadeTotal = Itens.Sum(x => x.Quantidade);
        var fretePorUnidade = quantidadeTotal <= 0 ? 0 : FreteEntregaClienteReais / quantidadeTotal;
        return PrecoVendaSemFreteItemReais(item) / item.Quantidade + fretePorUnidade;
    }

    public decimal PrecoUnitarioComTodosImpostos(ItemCotacao item) =>
        PrecoUnitarioComImpostosSemIpi(item) * (1 + Taxa(item.Impostos.IpiVendaPercentual));

    public decimal FatorMultiplicador(ItemCotacao item) => 1 - Taxa(
        Calculo.MarkupPercentual + Calculo.IrCsllPercentual + item.Impostos.IcmsVendaPercentual +
        item.Impostos.PisVendaPercentual + item.Impostos.CofinsVendaPercentual);

    public ResultadoImportacaoItem CalcularImportacao(ItemCotacao item)
    {
        var proporcao = ProporcaoItem(item);
        var cif = ValorCifReais * proporcao;
        var siscomex = Calculo.TaxaSiscomexReais * proporcao;
        var despesas = TotalDespesasAduaneirasReais * proporcao;
        var ii = cif * Taxa(item.Impostos.ImpostoImportacaoPercentual);
        var ipi = (cif + ii) * Taxa(item.Impostos.IpiPercentual);
        var pis = cif * Taxa(item.Impostos.PisPercentual);
        var cofins = cif * Taxa(item.Impostos.CofinsPercentual);
        var baseIcms = cif + ii + ipi + siscomex + pis + cofins;
        var aliquotaIcms = Taxa(item.Impostos.IcmsPercentual);
        var icms = aliquotaIcms is > 0 and < 1 ? baseIcms / (1 - aliquotaIcms) * aliquotaIcms : 0;
        var creditos = (item.Impostos.AproveitaCreditoIpi ? ipi : 0) +
                       (item.Impostos.AproveitaCreditoPis ? pis : 0) +
                       (item.Impostos.AproveitaCreditoCofins ? cofins : 0) +
                       (item.Impostos.AproveitaCreditoIcms ? icms : 0);
        return new(cif, siscomex, despesas, ii, ipi, pis, cofins, icms, creditos);
    }

    private decimal CustoFinalItemReais(ItemCotacao item)
    {
        var importacao = CalcularImportacao(item);
        var entradaLiquida = importacao.ValorNotaEntradaReais - importacao.TotalCreditosReais;
        var proporcao = ValorNotaFiscalEntradaReais <= 0
            ? ProporcaoItem(item)
            : importacao.ValorNotaEntradaReais / ValorNotaFiscalEntradaReais;
        return entradaLiquida + (CustoOperacionalReais + CustoFinanceiroReais) * proporcao;
    }

    private decimal PrecoVendaSemFreteItemReais(ItemCotacao item)
    {
        var fator = FatorMultiplicador(item);
        return fator <= 0 ? 0 : CustoFinalItemReais(item) / fator;
    }

    private decimal ProporcaoItem(ItemCotacao item)
    {
        var fob = ValorFobUsd(item);
        if (FobTotalUsd > 0) return fob / FobTotalUsd;
        var quantidadeTotal = Itens.Sum(x => x.Quantidade);
        return quantidadeTotal > 0 ? item.Quantidade / quantidadeTotal : 0;
    }

    private static decimal ValorFobUsd(ItemCotacao item) => item.Quantidade * item.PrecoUnitarioUsd;
    private static decimal Taxa(decimal percentual) => percentual / 100m;
}

public sealed record ResultadoImportacaoItem(
    decimal ValorCifReais,
    decimal TaxaSiscomexReais,
    decimal DespesasAduaneirasReais,
    decimal ImpostoImportacaoReais,
    decimal IpiReais,
    decimal PisReais,
    decimal CofinsReais,
    decimal IcmsReais,
    decimal TotalCreditosReais)
{
    public decimal TotalTributosReais => ImpostoImportacaoReais + IpiReais + PisReais + CofinsReais + IcmsReais;
    public decimal ValorNotaEntradaReais => ValorCifReais + TaxaSiscomexReais + DespesasAduaneirasReais + TotalTributosReais;
}

public sealed class CalculoCotacao
{
    public ModalidadeCarga ModalidadeCarga { get; set; } = ModalidadeCarga.Lcl;
    public decimal PesoCargaKg { get; set; }
    public decimal QuantidadeContainers { get; set; } = 1;
    public decimal FreteInternacionalUsd { get; set; }
    public decimal SeguroInternacionalPercentual { get; set; } = 0.46m;
    public decimal ThcUsdPorKg { get; set; } = 0.00405m;
    public decimal ThcUsdPorContainer { get; set; }
    public decimal TaxaSiscomexReais { get; set; }
    public DespesasAduaneiras DespesasAduaneiras { get; set; } = new();
    public decimal CustoOperacionalPercentual { get; set; }
    public decimal CustoLogisticoPercentual { get; set; }
    public decimal CustoFinanceiroPercentual { get; set; }
    public decimal MarkupPercentual { get; set; }
    public decimal IrCsllPercentual { get; set; }
    public decimal ComissaoOrigemPercentual { get; set; }
    public decimal CubagemM3 { get; set; }
    public decimal ValorFretePorM3Reais { get; set; }
    public decimal PedagioReais { get; set; }
    public decimal AdValoremFretePercentual { get; set; }
    public decimal IcmsFretePercentual { get; set; }
    public bool UsarFreteEntregaManual { get; set; }
    public decimal FreteEntregaManualReais { get; set; }
}

public sealed class DespesasAduaneiras
{
    public decimal ArmazenagemReais { get; set; }
    public decimal DespachanteReais { get; set; }
    public decimal TaxaCambioReais { get; set; }
    public decimal AfrmmReais { get; set; }
    public decimal LiberacaoBlReais { get; set; }
    public decimal DesconsolidacaoReais { get; set; }
    public decimal IspsDropOffReais { get; set; }
    public decimal PosicionamentoContainerReais { get; set; }
    public decimal LevanteContainerReais { get; set; }
    public decimal PesagemContainerReais { get; set; }
    public decimal PrestacaoServicoReais { get; set; }
    public decimal SdaReais { get; set; }
    public decimal FretePortoArmazemReais { get; set; }
    public decimal TotalReais => ArmazenagemReais + DespachanteReais + TaxaCambioReais + AfrmmReais +
                                 LiberacaoBlReais + DesconsolidacaoReais + IspsDropOffReais +
                                 PosicionamentoContainerReais + LevanteContainerReais + PesagemContainerReais +
                                 PrestacaoServicoReais + SdaReais + FretePortoArmazemReais;
}

public sealed class ModeloCotacao
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public string Nome { get; set; } = "";
    [Required] public string CodigoFornecedor { get; set; } = "";
    public bool ItemAdicionado { get; set; }
    public ItemCotacao Item { get; set; } = new();
}

public sealed class ItemCotacao
{
    public byte[]? Foto { get; set; }
    [Required] public string Descricao { get; set; } = "";
    [Required] public string Codigo { get; set; } = "";
    [Required] public string Ncm { get; set; } = "";
    public string Material { get; set; } = "";
    public string Tamanho { get; set; } = "";
    public string Especificacao { get; set; } = "";
    public string Capacidade { get; set; } = "";
    public decimal Quantidade { get; set; } = 1;
    public decimal PrecoUnitarioUsd { get; set; }
    public Impostos Impostos { get; set; } = new();
}

public sealed class Impostos
{
    public decimal ImpostoImportacaoPercentual { get; set; }
    public decimal IpiPercentual { get; set; }
    public decimal PisPercentual { get; set; }
    public decimal CofinsPercentual { get; set; }
    public decimal IcmsPercentual { get; set; }
    public decimal IpiVendaPercentual { get; set; }
    public decimal PisVendaPercentual { get; set; }
    public decimal CofinsVendaPercentual { get; set; }
    public decimal IcmsVendaPercentual { get; set; }
    public bool AproveitaCreditoIpi { get; set; } = true;
    public bool AproveitaCreditoPis { get; set; } = true;
    public bool AproveitaCreditoCofins { get; set; } = true;
    public bool AproveitaCreditoIcms { get; set; } = true;
    public decimal TotalSemIpiPercentual => ImpostoImportacaoPercentual + PisPercentual + CofinsPercentual + IcmsPercentual;
    public decimal TotalPercentual => TotalSemIpiPercentual + IpiPercentual;
    public decimal TotalVendaSemIpiPercentual => PisVendaPercentual + CofinsVendaPercentual + IcmsVendaPercentual;
}

public static class StatusCotacao
{
    public const string EmPreenchimento = "Em preenchimento";
    public const string EmAnalise = "Em análise";
    public const string AguardandoCliente = "Aguardando cliente";
    public const string Aprovada = "Aprovada";
    public const string Recusada = "Recusada";
    public static readonly string[] Todos = [EmPreenchimento, EmAnalise, AguardandoCliente, Aprovada, Recusada];
}
