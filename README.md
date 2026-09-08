# Sistema de Cotações Full Brands

Aplicação Blazor Server em .NET 8 com backend C#, PostgreSQL e autenticação no Supabase.

## Execução local

As configurações sensíveis devem ser armazenadas com `dotnet user-secrets`:

- `Supabase:Url`
- `Supabase:PublishableKey`
- `Supabase:SecretKey`
- `ConnectionStrings:Supabase`

Na raiz do workspace:

```powershell
.\.dotnet\dotnet.exe run --project .\Cotacoes.Web\Cotacoes.Web.csproj
```

## Publicação

O projeto contém `Dockerfile` e `render.yaml`. Em produção, configure estas variáveis no serviço de hospedagem:

- `Supabase__Url`
- `Supabase__PublishableKey`
- `Supabase__SecretKey`
- `ConnectionStrings__Supabase`

Os valores nunca devem ser enviados ao GitHub. O endpoint `/health` pode ser usado pela hospedagem para verificar a disponibilidade da aplicação.
