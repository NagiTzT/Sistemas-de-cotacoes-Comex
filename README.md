# Sistema de Cotações Full Brands

Aplicação Blazor Server em .NET 8 com backend C#, PostgreSQL e autenticação no Supabase.

## Formação da cotação

- Modalidades de carga LCL e FCL.
- PTAX de venda do dólar preenchida automaticamente pelo Banco Central ao iniciar uma nova cotação, com edição manual disponível.
- Custos ligados na sequência: FOB, frete e seguro internacionais, THC, CIF, tributos de importação, despesas aduaneiras, créditos, custos operacional/logístico/financeiro, markup e tributos da venda.
- Impostos de importação e venda definidos por item/NCM.
- Frete de entrega calculado por cubagem ou informado manualmente.
- PDFs Full Beauty e Full Pharma com preços unitários e valor total do pedido.
- Aba Informações do PDF para editar prazos e condições por cotação, com opção de restaurar o padrão.
- Etapa Prévia e Fotos com uma foto opcional por produto (JPG/PNG/WebP até 10 MB, convertida para JPEG de até 1200 px e 1 MB). A imagem é armazenada no banco e posicionada ao lado dos dados do produto nos dois PDFs.

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

## Banco de dados

Execute os scripts SQL de `Database/Migrations` no SQL Editor do Supabase, em ordem numérica. A migração `002_calculo_importacao_completo.sql` adiciona a data de referência da PTAX e os detalhes de cálculo e impostos em JSONB, preservando os registros já existentes.

A migração `003_fotos_textos_pdf.sql` adiciona os textos editáveis da proposta e a foto de cada item. Execute antes de publicar a versão com anexos.

A migração `004_preservar_cotacoes_emitidas.sql` registra a primeira geração do PDF. Antes dela, editar atualiza a própria cotação. Depois dela, editar abre uma nova proposta preenchida, numerada ao salvar, preservando a original. Fotos, textos e valores são copiados; a nova proposta começa em preenchimento. Mudanças de status continuam disponíveis. A regra vale para PDFs gerados a partir desta versão, pois emissões anteriores não tinham registro próprio.

## Publicação

O projeto contém `Dockerfile` e `render.yaml`. Em produção, configure estas variáveis no serviço de hospedagem:

- `Supabase__Url`
- `Supabase__PublishableKey`
- `Supabase__SecretKey`
- `ConnectionStrings__Supabase`

Os valores nunca devem ser enviados ao GitHub. O endpoint `/health` pode ser usado pela hospedagem para verificar a disponibilidade da aplicação.
