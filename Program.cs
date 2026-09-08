using System.Security.Claims;
using Cotacoes.Web.Components;
using Cotacoes.Web.Models;
using Cotacoes.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using QuestPDF.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(opcoes =>
    {
        opcoes.LoginPath = "/login";
        opcoes.AccessDeniedPath = "/login";
        opcoes.Cookie.Name = "Cotacoes.Autenticacao";
        opcoes.Cookie.HttpOnly = true;
        opcoes.Cookie.SameSite = SameSiteMode.Lax;
        opcoes.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        opcoes.ExpireTimeSpan = TimeSpan.FromHours(8);
        opcoes.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp =>
{
    var configuracao = sp.GetRequiredService<IConfiguration>();
    var ambiente = sp.GetRequiredService<IWebHostEnvironment>();
    var opcoes = configuracao.GetSection("Supabase").Get<SupabaseOptions>() ?? new();
    opcoes.ConnectionString = configuracao.GetConnectionString("Supabase") ?? "";
    if (!ambiente.IsDevelopment() && !opcoes.AuthConfigurado)
        throw new InvalidOperationException("As configurações do Supabase são obrigatórias em produção.");
    return opcoes;
});
builder.Services.AddSingleton<CotacaoService>();
builder.Services.AddSingleton<PdfService>();
builder.Services.AddSingleton<UsuarioService>();
QuestPDF.Settings.License = LicenseType.Community;

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapPost("/auth/login", async (HttpContext contexto, UsuarioService usuarios) =>
{
    var formulario = await contexto.Request.ReadFormAsync();
    var login = formulario["login"].ToString();
    var senha = formulario["senha"].ToString();
    var retorno = UrlLocal(formulario["returnUrl"].ToString());
    var usuario = await usuarios.AutenticarAsync(login, senha);

    if (usuario is null)
        return Results.LocalRedirect($"/login?erro=1&returnUrl={Uri.EscapeDataString(retorno)}");

    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, usuario.Id.ToString()),
        new(ClaimTypes.Name, usuario.Nome),
        new("login", usuario.Login),
        new(ClaimTypes.Role, usuario.Perfil)
    };
    var identidade = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await contexto.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identidade));
    return Results.LocalRedirect(retorno);
}).AllowAnonymous().DisableAntiforgery();

app.MapPost("/auth/logout", async (HttpContext contexto) =>
{
    await contexto.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/login");
}).RequireAuthorization().DisableAntiforgery();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
if (app.Environment.IsDevelopment())
{
    app.MapGet("/dev/storage-status", (CotacaoService cotacoes, UsuarioService usuarios) => Results.Ok(new
    {
        origem = cotacoes.UsandoSupabase ? "Supabase" : "JSON local",
        cotacoes = cotacoes.Listar().Count,
        usuarios = usuarios.Listar().Count
    })).AllowAnonymous();
}

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/api/cotacoes/{id:guid}/pdf", (Guid id, CotacaoService service, PdfService pdf) =>
{
    var cotacao = service.Obter(id);
    if (cotacao is null) return Results.NotFound();
    service.AtualizarStatus(id, StatusCotacao.AguardandoCliente);
    return Results.File(pdf.Gerar(cotacao), "application/pdf", $"cotacao-{cotacao.Numero}.pdf");
}).RequireAuthorization();

_ = app.Services.GetRequiredService<CotacaoService>();
app.Run();

static string UrlLocal(string? retorno) =>
    !string.IsNullOrWhiteSpace(retorno) && retorno.StartsWith('/') && !retorno.StartsWith("//") ? retorno : "/";
