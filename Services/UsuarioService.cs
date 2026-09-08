using System.Net.Http.Json;
using System.Text.Json;
using Cotacoes.Web.Models;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace Cotacoes.Web.Services;

public sealed class UsuarioService
{
    private readonly object _sync = new();
    private readonly string _arquivo;
    private readonly SupabaseOptions _supabase;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PasswordHasher<Usuario> _hasher = new();
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private List<Usuario> _usuarios;

    public bool UsandoSupabase => _supabase.AuthConfigurado;

    public UsuarioService(
        IWebHostEnvironment ambiente,
        IConfiguration configuracao,
        SupabaseOptions supabase,
        IHttpClientFactory httpClientFactory)
    {
        _supabase = supabase;
        _httpClientFactory = httpClientFactory;
        var pasta = Path.Combine(ambiente.ContentRootPath, "App_Data");
        Directory.CreateDirectory(pasta);
        _arquivo = Path.Combine(pasta, "usuarios.json");
        _usuarios = CarregarLocal();
        if (!UsandoSupabase) CriarAdministradorInicial(configuracao.GetSection("AdministradorInicial"));
    }

    public async Task<UsuarioAutenticado?> AutenticarAsync(string login, string senha)
    {
        if (!UsandoSupabase) return AutenticarLocal(login, senha);

        using var requisicao = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_supabase.Url.TrimEnd('/')}/auth/v1/token?grant_type=password");
        requisicao.Headers.Add("apikey", _supabase.PublishableKey);
        requisicao.Content = JsonContent.Create(new { email = login.Trim(), password = senha });

        using var resposta = await _httpClientFactory.CreateClient().SendAsync(requisicao);
        if (!resposta.IsSuccessStatusCode) return null;

        using var documento = JsonDocument.Parse(await resposta.Content.ReadAsStreamAsync());
        if (!documento.RootElement.TryGetProperty("user", out var usuarioJson)
            || !usuarioJson.TryGetProperty("id", out var idJson)
            || !Guid.TryParse(idJson.GetString(), out var id)) return null;

        return ObterPerfilSupabase(id, login.Trim());
    }

    public IReadOnlyList<UsuarioResumo> Listar()
    {
        if (!UsandoSupabase)
        {
            lock (_sync)
                return _usuarios.OrderBy(x => x.Nome)
                    .Select(x => new UsuarioResumo(x.Id, x.Nome, x.Login, x.Perfil, x.Ativo, x.CriadoEmUtc))
                    .ToList();
        }

        var usuarios = new List<UsuarioResumo>();
        using var conexao = AbrirConexao();
        const string sql = """
            select p.usuario_id, p.nome, coalesce(u.email, ''), p.perfil, p.ativo, p.criado_em
              from public.cot_perfis p
              left join auth.users u on u.id = p.usuario_id
             order by p.nome
            """;
        using var comando = new NpgsqlCommand(sql, conexao);
        using var leitor = comando.ExecuteReader();
        while (leitor.Read())
            usuarios.Add(new(
                leitor.GetGuid(0), leitor.GetString(1), leitor.GetString(2), leitor.GetString(3),
                leitor.GetBoolean(4), leitor.GetDateTime(5)));
        return usuarios;
    }

    public async Task<(bool Sucesso, string Mensagem)> CadastrarAsync(string nome, string email, string senha, string perfil)
    {
        nome = nome.Trim();
        email = email.Trim().ToLowerInvariant();
        var validacao = ValidarCadastro(nome, email, senha, perfil, UsandoSupabase);
        if (validacao is not null) return (false, validacao);
        if (!UsandoSupabase) return CadastrarLocal(nome, email, senha, perfil);

        using var requisicao = new HttpRequestMessage(HttpMethod.Post, $"{_supabase.Url.TrimEnd('/')}/auth/v1/admin/users");
        requisicao.Headers.Add("apikey", _supabase.SecretKey);
        if (_supabase.SecretKey.StartsWith("eyJ", StringComparison.Ordinal))
            requisicao.Headers.Authorization = new("Bearer", _supabase.SecretKey);
        requisicao.Content = JsonContent.Create(new
        {
            email,
            password = senha,
            email_confirm = true,
            user_metadata = new { nome }
        });
        using var resposta = await _httpClientFactory.CreateClient().SendAsync(requisicao);
        if (!resposta.IsSuccessStatusCode)
        {
            var erro = await LerMensagemErro(resposta);
            return (false, erro.Contains("already", StringComparison.OrdinalIgnoreCase)
                ? "Este e-mail já está cadastrado."
                : "Não foi possível criar o usuário no Supabase Auth.");
        }

        using var documento = JsonDocument.Parse(await resposta.Content.ReadAsStreamAsync());
        var raiz = documento.RootElement;
        if (raiz.TryGetProperty("user", out var usuarioAninhado)) raiz = usuarioAninhado;
        if (!raiz.TryGetProperty("id", out var idJson) || !Guid.TryParse(idJson.GetString(), out var id))
            return (false, "O Supabase criou a conta, mas não retornou o identificador do usuário.");

        try
        {
            using var conexao = AbrirConexao();
            const string sql = """
                insert into public.cot_perfis (usuario_id, nome, perfil, ativo)
                values (@id, @nome, @perfil, true)
                on conflict (usuario_id) do update set nome = excluded.nome, perfil = excluded.perfil, ativo = true
                """;
            using var comando = new NpgsqlCommand(sql, conexao);
            comando.Parameters.AddWithValue("id", id);
            comando.Parameters.AddWithValue("nome", nome);
            comando.Parameters.AddWithValue("perfil", perfil);
            comando.ExecuteNonQuery();
            return (true, "Usuário cadastrado. Se a confirmação de e-mail estiver ativa, ele deverá confirmar o endereço antes de entrar.");
        }
        catch (PostgresException)
        {
            return (false, "A conta foi criada no Auth, mas não foi possível gravar o perfil de acesso.");
        }
    }

    private UsuarioAutenticado? ObterPerfilSupabase(Guid id, string email)
    {
        using var conexao = AbrirConexao();
        const string sql = "select nome, perfil, ativo from public.cot_perfis where usuario_id = @id";
        using var comando = new NpgsqlCommand(sql, conexao);
        comando.Parameters.AddWithValue("id", id);
        using var leitor = comando.ExecuteReader();
        if (!leitor.Read() || !leitor.GetBoolean(2)) return null;
        return new(id, leitor.GetString(0), email, leitor.GetString(1));
    }

    private NpgsqlConnection AbrirConexao()
    {
        var conexao = new NpgsqlConnection(_supabase.ConnectionString);
        conexao.Open();
        return conexao;
    }

    private UsuarioAutenticado? AutenticarLocal(string login, string senha)
    {
        lock (_sync)
        {
            var usuario = _usuarios.FirstOrDefault(x => x.Ativo && string.Equals(x.Login, NormalizarLogin(login), StringComparison.OrdinalIgnoreCase));
            if (usuario is null) return null;
            var resultado = _hasher.VerifyHashedPassword(usuario, usuario.SenhaHash, senha);
            if (resultado == PasswordVerificationResult.Failed) return null;
            return new(usuario.Id, usuario.Nome, usuario.Login, usuario.Perfil);
        }
    }

    private (bool Sucesso, string Mensagem) CadastrarLocal(string nome, string login, string senha, string perfil)
    {
        login = NormalizarLogin(login);
        lock (_sync)
        {
            if (_usuarios.Any(x => string.Equals(x.Login, login, StringComparison.OrdinalIgnoreCase)))
                return (false, "Este usuário já está cadastrado.");
            var usuario = new Usuario { Nome = nome, Login = login, Perfil = perfil };
            usuario.SenhaHash = _hasher.HashPassword(usuario, senha);
            _usuarios.Add(usuario);
            PersistirLocal();
            return (true, "Usuário cadastrado com sucesso.");
        }
    }

    private static string? ValidarCadastro(string nome, string login, string senha, string perfil, bool exigirEmail)
    {
        if (string.IsNullOrWhiteSpace(nome) || string.IsNullOrWhiteSpace(login)) return "Preencha o nome e o e-mail.";
        if (exigirEmail && !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(login)) return "Informe um e-mail válido.";
        if (senha.Length < 8) return "A senha deve ter pelo menos 8 caracteres.";
        if (!PerfisUsuario.Todos.Contains(perfil)) return "Selecione um perfil válido.";
        return null;
    }

    private static async Task<string> LerMensagemErro(HttpResponseMessage resposta)
    {
        try
        {
            using var documento = JsonDocument.Parse(await resposta.Content.ReadAsStreamAsync());
            foreach (var campo in new[] { "msg", "message", "error_description", "error" })
                if (documento.RootElement.TryGetProperty(campo, out var valor)) return valor.GetString() ?? "";
        }
        catch (JsonException) { }
        return "";
    }

    private void CriarAdministradorInicial(IConfigurationSection configuracao)
    {
        lock (_sync)
        {
            if (_usuarios.Any()) return;

            var login = configuracao["Usuario"];
            var senha = configuracao["Senha"];
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(senha)) return;

            var usuario = new Usuario
            {
                Nome = configuracao["Nome"] ?? "Administrador",
                Login = NormalizarLogin(login),
                Perfil = PerfisUsuario.Administrador
            };
            usuario.SenhaHash = _hasher.HashPassword(usuario, senha);
            _usuarios.Add(usuario);
            PersistirLocal();
        }
    }

    private List<Usuario> CarregarLocal()
    {
        if (!File.Exists(_arquivo)) return [];
        try { return JsonSerializer.Deserialize<List<Usuario>>(File.ReadAllText(_arquivo), _json) ?? []; }
        catch (JsonException) { return []; }
    }

    private void PersistirLocal()
    {
        var temporario = _arquivo + ".tmp";
        File.WriteAllText(temporario, JsonSerializer.Serialize(_usuarios, _json));
        File.Move(temporario, _arquivo, true);
    }

    private static string NormalizarLogin(string login) => login.Trim().ToLowerInvariant();
}
