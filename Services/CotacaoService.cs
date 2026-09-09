using System.Globalization;
using System.Text.Json;
using Cotacoes.Web.Models;
using Npgsql;
using NpgsqlTypes;

namespace Cotacoes.Web.Services;

public sealed class CotacaoService
{
    private readonly object _sync = new();
    private readonly string _arquivo;
    private readonly string? _conexao;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, IgnoreReadOnlyProperties = true };
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
            if (UsandoSupabase) SalvarNoSupabase(cotacao, criadoPor);
            else
            {
                if (_cotacoes.Any(x => x.Id == cotacao.Id && x.PdfGeradoEmUtc.HasValue))
                    TransformarEmCopia(cotacao, criadoPor);
                SalvarNoJson(cotacao);
            }
            var index = _cotacoes.FindIndex(x => x.Id == cotacao.Id);
            if (index >= 0) _cotacoes[index] = cotacao; else _cotacoes.Add(cotacao);
            return cotacao;
        }
    }

    public Cotacao? PrepararEdicao(Guid id)
    {
        lock (_sync)
        {
            if (UsandoSupabase) _cotacoes = CarregarDoSupabase();
            var original = _cotacoes.FirstOrDefault(x => x.Id == id);
            if (original is null) return null;
            var copia = JsonSerializer.Deserialize<Cotacao>(JsonSerializer.Serialize(original, _json), _json)!;
            if (original.PdfGeradoEmUtc.HasValue) TransformarEmCopia(copia, null, numerar: false);
            return copia;
        }
    }

    private void TransformarEmCopia(Cotacao cotacao, Guid? criadoPor, bool numerar = true)
    {
        cotacao.CotacaoOrigemId = cotacao.Id;
        cotacao.Id = Guid.NewGuid();
        cotacao.Numero = numerar ? $"COT-{DateTime.Today:yyyy}-{_sequencia++:D4}" : "";
        cotacao.PdfGeradoEmUtc = null;
        cotacao.CriadoPorId = criadoPor;
        cotacao.Status = StatusCotacao.EmPreenchimento;
        foreach (var modelo in cotacao.Modelos) modelo.Id = Guid.NewGuid();
    }

    public (byte[] Conteudo, string Numero)? GerarPdf(Guid id, PdfService pdf)
    {
        lock (_sync)
        {
            using var conexao = UsandoSupabase ? AbrirConexao() : null;
            using var transacao = conexao?.BeginTransaction();
            if (conexao is not null)
            {
                using var bloquear = new NpgsqlCommand("select id from public.cot_cotacoes where id = @id for update", conexao, transacao);
                bloquear.Parameters.AddWithValue("id", id);
                if (bloquear.ExecuteScalar() is null) return null;
                _cotacoes = CarregarDoSupabase();
            }
            var original = _cotacoes.FirstOrDefault(x => x.Id == id);
            if (original is null) return null;
            var copia = JsonSerializer.Deserialize<Cotacao>(JsonSerializer.Serialize(original, _json), _json)!;
            // Só preserva a emissão depois que o documento foi gerado com sucesso.
            var conteudo = pdf.Gerar(copia);
            if (!copia.PdfGeradoEmUtc.HasValue)
            {
                copia.PdfGeradoEmUtc = DateTime.UtcNow;
                copia.Status = StatusCotacao.AguardandoCliente;
                if (conexao is not null)
                {
                    using var atualizar = new NpgsqlCommand("update public.cot_cotacoes set pdf_gerado_em_utc = @data, status = @status where id = @id", conexao, transacao);
                    atualizar.Parameters.AddWithValue("data", copia.PdfGeradoEmUtc.Value);
                    atualizar.Parameters.AddWithValue("status", copia.Status);
                    atualizar.Parameters.AddWithValue("id", id);
                    atualizar.ExecuteNonQuery();
                }
                else SalvarNoJson(copia);
            }
            transacao?.Commit();
            _cotacoes[_cotacoes.FindIndex(x => x.Id == id)] = copia;
            return (conteudo, copia.Numero);
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
                   c.data_referencia_cambio, c.calculo_detalhado::text,
                   m.id, m.nome, m.codigo_fornecedor,
                   i.descricao, i.codigo_produto, i.ncm, i.material, i.tamanho, i.especificacao,
                   i.capacidade, i.quantidade, i.preco_unitario_usd,
                   i.imposto_importacao_percentual, i.ipi_percentual, i.pis_percentual,
                   i.cofins_percentual, i.icms_percentual, i.impostos_detalhados::text,
                   c.prazos_pdf, c.informacoes_pdf, i.foto, c.pdf_gerado_em_utc, c.cotacao_origem_id
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
                    PdfGeradoEmUtc = leitor.IsDBNull(35) ? null : leitor.GetDateTime(35),
                    CotacaoOrigemId = leitor.IsDBNull(36) ? null : leitor.GetGuid(36),
                    PrazosPdf = leitor.IsDBNull(32) ? null : leitor.GetString(32),
                    InformacoesPdf = leitor.IsDBNull(33) ? null : leitor.GetString(33),
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
                    DataReferenciaCambio = leitor.IsDBNull(12) ? null : DateOnly.FromDateTime(leitor.GetDateTime(12)),
                    Calculo = leitor.IsDBNull(13) ? new() : JsonSerializer.Deserialize<CalculoCotacao>(leitor.GetString(13), _json) ?? new(),
                    Modelos = []
                };
                if ((leitor.IsDBNull(13) || leitor.GetString(13) == "{}") && cotacao.MargemPercentual != 0)
                    cotacao.Calculo.MarkupPercentual = cotacao.MargemPercentual;
                porId[id] = cotacao;
                resultado.Add(cotacao);
            }

            if (leitor.IsDBNull(14)) continue;
            var modelo = new ModeloCotacao
            {
                Id = leitor.GetGuid(14),
                Nome = leitor.GetString(15),
                CodigoFornecedor = leitor.GetString(16),
                ItemAdicionado = !leitor.IsDBNull(17)
            };
            if (modelo.ItemAdicionado)
            {
                var impostos = new Impostos
                {
                    ImpostoImportacaoPercentual = leitor.GetDecimal(26),
                    IpiPercentual = leitor.GetDecimal(27),
                    PisPercentual = leitor.GetDecimal(28),
                    CofinsPercentual = leitor.GetDecimal(29),
                    IcmsPercentual = leitor.GetDecimal(30),
                    IpiVendaPercentual = leitor.GetDecimal(27),
                    PisVendaPercentual = leitor.GetDecimal(28),
                    CofinsVendaPercentual = leitor.GetDecimal(29),
                    IcmsVendaPercentual = leitor.GetDecimal(30),
                    AproveitaCreditoIpi = false,
                    AproveitaCreditoPis = false,
                    AproveitaCreditoCofins = false,
                    AproveitaCreditoIcms = false
                };
                if (!leitor.IsDBNull(31) && leitor.GetString(31) != "{}")
                    impostos = JsonSerializer.Deserialize<Impostos>(leitor.GetString(31), _json) ?? impostos;
                modelo.Item = new ItemCotacao
                {
                    Descricao = leitor.GetString(17),
                    Foto = leitor.IsDBNull(34) ? null : leitor.GetFieldValue<byte[]>(34),
                    Codigo = leitor.GetString(18),
                    Ncm = leitor.GetString(19),
                    Material = leitor.IsDBNull(20) ? "" : leitor.GetString(20),
                    Tamanho = leitor.IsDBNull(21) ? "" : leitor.GetString(21),
                    Especificacao = leitor.IsDBNull(22) ? "" : leitor.GetString(22),
                    Capacidade = leitor.IsDBNull(23) ? "" : leitor.GetString(23),
                    Quantidade = leitor.GetDecimal(24),
                    PrecoUnitarioUsd = leitor.GetDecimal(25),
                    Impostos = impostos
                };
            }
            cotacao.Modelos.Add(modelo);
        }
        return resultado;
    }

    private void SalvarNoSupabase(Cotacao cotacao, Guid? criadoPor = null)
    {
        using var conexao = AbrirConexao();
        using var transacao = conexao.BeginTransaction();
        // Protege também formulários abertos antes da emissão e outras instâncias do servidor.
        using (var bloquear = new NpgsqlCommand("select pdf_gerado_em_utc from public.cot_cotacoes where id = @id for update", conexao, transacao))
        {
            bloquear.Parameters.AddWithValue("id", cotacao.Id);
            var emissao = bloquear.ExecuteScalar();
            if (emissao is not null && emissao is not DBNull) TransformarEmCopia(cotacao, criadoPor);
        }
        const string sqlCotacao = """
            insert into public.cot_cotacoes
                (id, numero, criado_por, cliente, destino, origem, data_cotacao, cambio, modelo_pdf,
                 despesas_adicionais_reais, margem_percentual, status, data_referencia_cambio, calculo_detalhado, prazos_pdf, informacoes_pdf, pdf_gerado_em_utc, cotacao_origem_id)
            values
                (@id, @numero, @criado_por, @cliente, @destino, @origem, @data, @cambio, @modelo_pdf,
                 @despesas, @margem, @status, @data_cambio, @calculo, @prazos, @informacoes, @emissao, @origem_id)
            on conflict (id) do update set
                numero = excluded.numero, criado_por = coalesce(cot_cotacoes.criado_por, excluded.criado_por),
                cliente = excluded.cliente, destino = excluded.destino,
                origem = excluded.origem, data_cotacao = excluded.data_cotacao, cambio = excluded.cambio,
                modelo_pdf = excluded.modelo_pdf, despesas_adicionais_reais = excluded.despesas_adicionais_reais,
                margem_percentual = excluded.margem_percentual, status = excluded.status,
                data_referencia_cambio = excluded.data_referencia_cambio, calculo_detalhado = excluded.calculo_detalhado,
                prazos_pdf = excluded.prazos_pdf, informacoes_pdf = excluded.informacoes_pdf
            """;
        using (var comando = new NpgsqlCommand(sqlCotacao, conexao, transacao))
        {
            comando.Parameters.AddWithValue("id", cotacao.Id);
            comando.Parameters.AddWithValue("numero", cotacao.Numero);
            comando.Parameters.Add("emissao", NpgsqlDbType.TimestampTz).Value = (object?)cotacao.PdfGeradoEmUtc ?? DBNull.Value;
            comando.Parameters.Add("origem_id", NpgsqlDbType.Uuid).Value = (object?)cotacao.CotacaoOrigemId ?? DBNull.Value;
            comando.Parameters.Add("prazos", NpgsqlDbType.Text).Value = (object?)cotacao.PrazosPdf ?? DBNull.Value;
            comando.Parameters.Add("informacoes", NpgsqlDbType.Text).Value = (object?)cotacao.InformacoesPdf ?? DBNull.Value;
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
            comando.Parameters.Add("data_cambio", NpgsqlDbType.Date).Value =
                (object?)cotacao.DataReferenciaCambio ?? DBNull.Value;
            comando.Parameters.Add("calculo", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(cotacao.Calculo, _json);
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

    private void InserirItem(NpgsqlConnection conexao, NpgsqlTransaction transacao, ModeloCotacao modelo)
    {
        const string sql = """
            insert into public.cot_itens
                (modelo_id, descricao, codigo_produto, ncm, material, tamanho, especificacao, capacidade,
                 quantidade, preco_unitario_usd, imposto_importacao_percentual, ipi_percentual,
                 pis_percentual, cofins_percentual, icms_percentual, impostos_detalhados, foto)
            values
                (@modelo_id, @descricao, @codigo, @ncm, @material, @tamanho, @especificacao, @capacidade,
                 @quantidade, @preco, @ii, @ipi, @pis, @cofins, @icms, @impostos, @foto)
            """;
        using var comando = new NpgsqlCommand(sql, conexao, transacao);
        comando.Parameters.AddWithValue("modelo_id", modelo.Id);
        comando.Parameters.Add("foto", NpgsqlDbType.Bytea).Value = (object?)modelo.Item.Foto ?? DBNull.Value;
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
        comando.Parameters.Add("impostos", NpgsqlDbType.Jsonb).Value =
            JsonSerializer.Serialize(modelo.Item.Impostos, _json);
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
