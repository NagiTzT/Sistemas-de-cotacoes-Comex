namespace Cotacoes.Web.Models;

public sealed class Usuario
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Nome { get; set; } = "";
    public string Login { get; set; } = "";
    public string SenhaHash { get; set; } = "";
    public string Perfil { get; set; } = PerfisUsuario.Comercial;
    public bool Ativo { get; set; } = true;
    public DateTime CriadoEmUtc { get; set; } = DateTime.UtcNow;
}

public sealed record UsuarioAutenticado(Guid Id, string Nome, string Login, string Perfil);
public sealed record UsuarioResumo(Guid Id, string Nome, string Login, string Perfil, bool Ativo, DateTime CriadoEmUtc);

public static class PerfisUsuario
{
    public const string Administrador = "Administrador";
    public const string Comercial = "Comercial";
    public static readonly string[] Todos = [Administrador, Comercial];
}
