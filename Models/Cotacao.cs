using System.ComponentModel.DataAnnotations;

namespace Cotacoes.Web.Models;

public enum ModeloPdf { FullBeauty, FullPharma }

public sealed class Cotacao
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Numero { get; set; } = "";
    public Guid? CriadoPorId { get; set; }
    [Required(ErrorMessage = "Informe o cliente.")] public string Cliente { get; set; } = "";
    [Required(ErrorMessage = "Informe o destino.")] public string Destino { get; set; } = "";
    [Required(ErrorMessage = "Informe a origem.")] public string Origem { get; set; } = "";
    public DateOnly DataCotacao { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    [Range(0.01, double.MaxValue, ErrorMessage = "Informe uma taxa de câmbio válida.")] public decimal Cambio { get; set; }
    public ModeloPdf ModeloPdf { get; set; } = ModeloPdf.FullBeauty;
    public List<ModeloCotacao> Modelos { get; set; } = [new()];
    public decimal DespesasAdicionaisReais { get; set; }
    public decimal MargemPercentual { get; set; }
    public string Status { get; set; } = "Em preenchimento";
    public decimal FobTotalUsd => Modelos.Where(x => x.ItemAdicionado).Sum(x => x.Item.Quantidade * x.Item.PrecoUnitarioUsd);
    public decimal FobTotalReais => FobTotalUsd * Cambio;
    public decimal TotalImpostosReais => Modelos.Where(x => x.ItemAdicionado).Sum(x =>
        x.Item.Quantidade * (PrecoUnitarioComTodosImpostos(x.Item) - PrecoUnitarioSemImpostos(x.Item)));
    public decimal CustoTotalReais => FobTotalReais + TotalImpostosReais + DespesasAdicionaisReais;
    public decimal PrecoVendaSugeridoReais => CustoTotalReais * (1 + MargemPercentual / 100);
    public decimal PrecoUnitarioSemImpostos(ItemCotacao item) => item.PrecoUnitarioUsd * Cambio;
    public decimal PrecoUnitarioComImpostosSemIpi(ItemCotacao item) => PrecoUnitarioSemImpostos(item) * (1 + item.Impostos.TotalSemIpiPercentual / 100);
    public decimal PrecoUnitarioComTodosImpostos(ItemCotacao item) => PrecoUnitarioSemImpostos(item) * (1 + item.Impostos.TotalPercentual / 100);
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
    public decimal TotalSemIpiPercentual => ImpostoImportacaoPercentual + PisPercentual + CofinsPercentual + IcmsPercentual;
    public decimal TotalPercentual => TotalSemIpiPercentual + IpiPercentual;
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
