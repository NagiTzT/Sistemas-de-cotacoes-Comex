using System.Globalization;
using System.Text.Json;
using Cotacoes.Web.Models;
using Npgsql;

namespace Cotacoes.Web.Services;

public sealed class CotacaoService
{
    private readonly object _sync = new();
    private readonly string _arquivo;
    private readonly string? _conexao;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private List<Cotacao> _cotacoes;
    private int _sequencia;

    public bool UsandoSupabase => !string.IsNullOrWhiteSpace(_conexao);

    public CotacaoService(IWebHostEnvironment ambiente, SupabaseOptions supabase)
    {
        var pasta = Path.Combine(ambiente.ContentRootPath, "App_Data");
        Directory.CreateDirectory(pasta);
        _arquivo = Path.Combine(pasta, "cotacoes.json");
        _conexao = supabase.BancoConfigurado ? supabase.ConnectionString : null;
        _cotacoes = UsandoSupabase ? CarregarDoSupabase() : CarregarDoJson();
        if (UsandoSupabase) MigrarJsonPendente();
        _sequencia = _cotacoes.Select(ExtrairSequencia).DefaultIfEmpty(0).Max() + 1;
    }

    public IReadOnlyList<Cotacao> Listar()
    {
        lock (_sync) return _cotacoes.OrderByDescending(x => x.DataCotacao).ToList();
    }

    public IReadOnlyList<Cotacao> ListarDoUsuario(Guid? usuarioId)
    {
        lock (_sync)
        {
            if (!UsandoSupabase) return _cotacoes.OrderByDescending(x => x.DataCotacao).ToList();
            if (usuarioId is null) return [];
            return _cotacoes.Where(x => x.CriadoPorId == usuarioId).OrderByDescending(x => x.DataCotacao).ToList();
        }
    }

    public Cotacao? Obter(Guid id)
    {
        lock (_sync) return _cotacoes.FirstOrDefault(x => x.Id == id);
    }

    public Cotacao Salvar(Cotacao cotacao, Guid? criadoPor = null)
    {
        lock (_sync)
        {
            if (string.IsNullOrEmpty(cotacao.Numero)) cotacao.Numero = $"COT-{DateTime.Today:yyyy}-{_sequencia++:D4}";
            cotacao.CriadoPorId ??= criadoPor;
            if (UsandoSupabase) SalvarNoSupabase(cotacao); else SalvarNoJson(cotacao);
            var index = _cotacoes.FindIndex(x => x.Id == cotacao.Id);
            if (index >= 0) _cotacoes[index] = cotacao; else _cotacoes.Add(cotacao);
            return cotacao;
        }
    }

    public bool AtualizarStatus(Guid id, string status)
    {
        if (!StatusCotacao.Todos.Contains(status)) return false;
        lock (_sync)
        {
            var cotacao = _cotacoes.FirstOrDefault(x => x.Id == id);
            if (cotacao is null) return false;
            if (UsandoSupabase)
            {
                using var conexao = AbrirConexao();
                using var comando = new NpgsqlCommand("update public.cot_cotacoes set status = @status where id = @id", conexao);
                comando.Parameters.AddWithValue("status", status);
                comando.Parameters.AddWithValue("id", id);
                if (comando.ExecuteNonQuery() == 0) return false;
            }
            cotacao.Status = status;
            if (!UsandoSupabase) PersistirJson();
            return true;
        }
    }

    private List<Cotacao> CarregarDoSupabase()
    {
        var resultado = new List<Cotacao>();
        using var conexao = AbrirConexao();
        const string sql = """
            select c.id, c.numero, c.cliente, c.destino, c.origem, c.data_cotacao, c.cambio,
                   c.modelo_pdf, c.despesas_adicionais_reais, c.margem_percentual, c.status, c.criado_por,
                   m.id, m.nome, m.codigo_fornecedor,
                   i.descricao, i.codigo_produto, i.ncm, i.material, i.tamanho, i.especificacao,
                   i.capacidade, i.quantidade, i.preco_unitario_usd,
                   i.imposto_importacao_percentual, i.ipi_percentual, i.pis_percentual,
                   i.cofins_percentual, i.icms_percentual
              from public.cot_cotacoes c
              left join public.cot_modelos m on m.cotacao_id = c.id
              left join public.cot_itens i on i.modelo_id = m.id
             order by c.data_cotacao desc, c.criado_em desc, m.ordem
            """;
        using var comando = new NpgsqlCommand(sql, conexao);
        using var leitor = comando.ExecuteReader();
        var porId = new Dictionary<Guid, Cotacao>();
        while (leitor.Read())
        {
            var id = leitor.GetGuid(0);
            if (!porId.TryGetValue(id, out var cotacao))
            {
                cotacao = new Cotacao
                {
                    Id = id,
                    Numero = leitor.GetString(1),
                    Cliente = leitor.GetString(2),
                    Destino = leitor.GetString(3),
                    Origem = leitor.GetString(4),
                    DataCotacao = DateOnly.FromDateTime(leitor.GetDateTime(5)),
                    Cambio = leitor.GetDecimal(6),
                    ModeloPdf = Enum.TryParse<ModeloPdf>(leitor.GetString(7), out var modeloPdf) ? modeloPdf : ModeloPdf.FullBeauty,
                    DespesasAdicionaisReais = leitor.GetDecimal(8),
                    MargemPercentual = leitor.GetDecimal(9),
                    Status = leitor.GetString(10),
                    CriadoPorId = leitor.IsDBNull(11) ? null : leitor.GetGuid(11),
                    Modelos = []
                };
                porId[id] = cotacao;
                resultado.Add(cotacao);
            }

            if (leitor.IsDBNull(12)) continue;
            var modelo = new ModeloCotacao
            {
                Id = leitor.GetGuid(12),
                Nome = leitor.GetString(13),
                CodigoFornecedor = leitor.GetString(14),
                ItemAdicionado = !leitor.IsDBNull(15)
            };
            if (modelo.ItemAdicionado)
            {
                modelo.Item = new ItemCotacao
                {
                    Descricao = leitor.GetString(15),
                    Codigo = leitor.GetString(16),
                    Ncm = leitor.GetString(17),
                    Material = leitor.IsDBNull(18) ? "" : leitor.GetString(18),
                    Tamanho = leitor.IsDBNull(19) ? "" : leitor.GetString(19),
                    Especificacao = leitor.IsDBNull(20) ? "" : leitor.GetString(20),
                    Capacidade = leitor.IsDBNull(21) ? "" : leitor.GetString(21),
                    Quantidade = leitor.GetDecimal(22),
                    PrecoUnitarioUsd = leitor.GetDecimal(23),
                    Impostos = new Impostos
                    {
                        ImpostoImportacaoPercentual = leitor.GetDecimal(24),
                        IpiPercentual = leitor.GetDecimal(25),
                        PisPercentual = leitor.GetDecimal(26),
                        CofinsPercentual = leitor.GetDecimal(27),
                        IcmsPercentual = leitor.GetDecimal(28)
                    }
                };
            }
            cotacao.Modelos.Add(modelo);
        }
        return resultado;
    }

    private void SalvarNoSupabase(Cotacao cotacao)
    {
        using var conexao = AbrirConexao();
        using var transacao = conexao.BeginTransaction();
        const string sqlCotacao = """
            insert into public.cot_cotacoes
                (id, numero, criado_por, cliente, destino, origem, data_cotacao, cambio, modelo_pdf,
                 despesas_adicionais_reais, margem_percentual, status)
            values
                (@id, @numero, @criado_por, @cliente, @destino, @origem, @data, @cambio, @modelo_pdf,
                 @despesas, @margem, @status)
            on conflict (id) do update set
                numero = excluded.numero, criado_por = coalesce(cot_cotacoes.criado_por, excluded.criado_por),
                cliente = excluded.cliente, destino = excluded.destino,
                origem = excluded.origem, data_cotacao = excluded.data_cotacao, cambio = excluded.cambio,
                modelo_pdf = excluded.modelo_pdf, despesas_adicionais_reais = excluded.despesas_adicionais_reais,
                margem_percentual = excluded.margem_percentual, status = excluded.status
            """;
        using (var comando = new NpgsqlCommand(sqlCotacao, conexao, transacao))
        {
            comando.Parameters.AddWithValue("id", cotacao.Id);
            comando.Parameters.AddWithValue("numero", cotacao.Numero);
            comando.Parameters.AddWithValue("criado_por", (object?)cotacao.CriadoPorId ?? DBNull.Value);
            comando.Parameters.AddWithValue("cliente", cotacao.Cliente);
            comando.Parameters.AddWithValue("destino", cotacao.Destino);
            comando.Parameters.AddWithValue("origem", cotacao.Origem);
            comando.Parameters.AddWithValue("data", cotacao.DataCotacao.ToDateTime(TimeOnly.MinValue));
            comando.Parameters.AddWithValue("cambio", cotacao.Cambio);
            comando.Parameters.AddWithValue("modelo_pdf", cotacao.ModeloPdf.ToString());
            comando.Parameters.AddWithValue("despesas", cotacao.DespesasAdicionaisReais);
            comando.Parameters.AddWithValue("margem", cotacao.MargemPercentual);
            comando.Parameters.AddWithValue("status", cotacao.Status);
            comando.ExecuteNonQuery();
        }

        using (var apagar = new NpgsqlCommand("delete from public.cot_modelos where cotacao_id = @id", conexao, transacao))
        {
            apagar.Parameters.AddWithValue("id", cotacao.Id);
            apagar.ExecuteNonQuery();
        }

        for (var indice = 0; indice < cotacao.Modelos.Count; indice++)
        {
            var modelo = cotacao.Modelos[indice];
            const string sqlModelo = """
                insert into public.cot_modelos (id, cotacao_id, nome, codigo_fornecedor, ordem)
                values (@id, @cotacao_id, @nome, @fornecedor, @ordem)
                """;
            using (var comando = new NpgsqlCommand(sqlModelo, conexao, transacao))
            {
                comando.Parameters.AddWithValue("id", modelo.Id);
                comando.Parameters.AddWithValue("cotacao_id", cotacao.Id);
                comando.Parameters.AddWithValue("nome", modelo.Nome);
                comando.Parameters.AddWithValue("fornecedor", modelo.CodigoFornecedor);
                comando.Parameters.AddWithValue("ordem", indice + 1);
                comando.ExecuteNonQuery();
            }
            if (!modelo.ItemAdicionado) continue;
            InserirItem(conexao, transacao, modelo);
        }
        transacao.Commit();
    }

    private void MigrarJsonPendente()
    {
        var locais = CarregarDoJson();
        if (locais.Count == 0) return;
        var idsExistentes = _cotacoes.Select(x => x.Id).ToHashSet();
        var pendentes = locais.Where(x => !idsExistentes.Contains(x.Id)).ToList();
        if (pendentes.Count == 0) return;

        Guid? administradorId = null;
        using (var conexao = AbrirConexao())
        using (var comando = new NpgsqlCommand(
            "select usuario_id from public.cot_perfis where perfil = 'Administrador' and ativo = true order by criado_em limit 1",
            conexao))
        {
            var resultado = comando.ExecuteScalar();
            if (resultado is Guid id) administradorId = id;
        }
        if (administradorId is null) return;

        foreach (var cotacao in pendentes)
        {
            cotacao.CriadoPorId ??= administradorId;
            SalvarNoSupabase(cotacao);
        }
        _cotacoes = CarregarDoSupabase();
    }

    private static void InserirItem(NpgsqlConnection conexao, NpgsqlTransaction transacao, ModeloCotacao modelo)
    {
        const string sql = """
            insert into public.cot_itens
                (modelo_id, descricao, codigo_produto, ncm, material, tamanho, especificacao, capacidade,
                 quantidade, preco_unitario_usd, imposto_importacao_percentual, ipi_percentual,
                 pis_percentual, cofins_percentual, icms_percentual)
            values
                (@modelo_id, @descricao, @codigo, @ncm, @material, @tamanho, @especificacao, @capacidade,
                 @quantidade, @preco, @ii, @ipi, @pis, @cofins, @icms)
            """;
        using var comando = new NpgsqlCommand(sql, conexao, transacao);
        comando.Parameters.AddWithValue("modelo_id", modelo.Id);
        comando.Parameters.AddWithValue("descricao", modelo.Item.Descricao);
        comando.Parameters.AddWithValue("codigo", modelo.Item.Codigo);
        comando.Parameters.AddWithValue("ncm", modelo.Item.Ncm);
        comando.Parameters.AddWithValue("material", (object?)modelo.Item.Material.NullSeVazio() ?? DBNull.Value);
        comando.Parameters.AddWithValue("tamanho", (object?)modelo.Item.Tamanho.NullSeVazio() ?? DBNull.Value);
        comando.Parameters.AddWithValue("especificacao", (object?)modelo.Item.Especificacao.NullSeVazio() ?? DBNull.Value);
        comando.Parameters.AddWithValue("capacidade", (object?)modelo.Item.Capacidade.NullSeVazio() ?? DBNull.Value);
        comando.Parameters.AddWithValue("quantidade", modelo.Item.Quantidade);
        comando.Parameters.AddWithValue("preco", modelo.Item.PrecoUnitarioUsd);
        comando.Parameters.AddWithValue("ii", modelo.Item.Impostos.ImpostoImportacaoPercentual);
        comando.Parameters.AddWithValue("ipi", modelo.Item.Impostos.IpiPercentual);
        comando.Parameters.AddWithValue("pis", modelo.Item.Impostos.PisPercentual);
        comando.Parameters.AddWithValue("cofins", modelo.Item.Impostos.CofinsPercentual);
        comando.Parameters.AddWithValue("icms", modelo.Item.Impostos.IcmsPercentual);
        comando.ExecuteNonQuery();
    }

    private NpgsqlConnection AbrirConexao()
    {
        var conexao = new NpgsqlConnection(_conexao);
        conexao.Open();
        return conexao;
    }

    private List<Cotacao> CarregarDoJson()
    {
        if (!File.Exists(_arquivo)) return [];
        try
        {
            var conteudo = File.ReadAllText(_arquivo);
            var cotacoes = JsonSerializer.Deserialize<List<Cotacao>>(conteudo, _json) ?? [];
            using var documento = JsonDocument.Parse(conteudo);
            var registros = documento.RootElement.EnumerateArray().ToArray();
            for (var indice = 0; indice < Math.Min(cotacoes.Count, registros.Length); indice++)
            {
                if (!registros[indice].TryGetProperty("Impostos", out var impostosJson)) continue;
                var impostosLegados = impostosJson.Deserialize<Impostos>(_json);
                if (impostosLegados is null || impostosLegados.TotalPercentual == 0) continue;
                foreach (var modelo in cotacoes[indice].Modelos.Where(x => x.Item.Impostos.TotalPercentual == 0))
                    modelo.Item.Impostos = impostosLegados;
            }
            return cotacoes;
        }
        catch (JsonException) { return []; }
    }

    private void SalvarNoJson(Cotacao cotacao)
    {
        var index = _cotacoes.FindIndex(x => x.Id == cotacao.Id);
        if (index >= 0) _cotacoes[index] = cotacao; else _cotacoes.Add(cotacao);
        PersistirJson();
    }

    private void PersistirJson()
    {
        var temporario = _arquivo + ".tmp";
        File.WriteAllText(temporario, JsonSerializer.Serialize(_cotacoes, _json));
        File.Move(temporario, _arquivo, true);
    }

    private static int ExtrairSequencia(Cotacao cotacao) =>
        int.TryParse(cotacao.Numero.Split('-').LastOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var numero) ? numero : 0;
}

file static class TextoExtensions
{
    public static string? NullSeVazio(this string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor;
}
