# Configuração da instância — Veiculando.WhiteLabel.Api

Cada instância WhiteLabel é um deploy próprio do mesmo BFF, distinguido por
configuração (ADR-WL-005). Este documento lista o que **precisa** vir do
ambiente.

## Variáveis obrigatórias

A aplicação **não sobe** sem estas três — a falha é explícita na inicialização,
com mensagem dizendo o que falta. É proposital: um valor padrão silencioso
reintroduziria o problema que a verificação existe para impedir.

| Variável | Para quê |
|---|---|
| `ConnectionStrings__Veiculando` | Banco do core. Carrega credenciais — nunca versionar. |
| `JwtSettings__Secret` | Assinatura HMAC-SHA256 dos JWTs. Mínimo de 32 caracteres. |
| `SeedAccount__Password` | Senha da conta de serviço que escreve no core. |

> O separador é **duplo sublinhado** (`__`), que o provider de configuração do
> ASP.NET Core traduz para o `:` da hierarquia do JSON.

### Por que o `JwtSettings__Secret` não tem default

O `appsettings.json` versionado trazia
`"WlCustomSecretKey_ChangeInProd_NeedsToBeAtLeast32Chars12345"`. Com um segredo
público no repositório, qualquer pessoa com acesso ao código **forja um JWT de
operador válido** — sem precisar de credencial nenhuma, e sem deixar rastro de
login. Por isso o campo agora é vazio e a subida falha se ele não vier do
ambiente.

Gere um valor aleatório por instância, por exemplo:

```bash
openssl rand -base64 48
```

## Variáveis por instância

| Chave | Valor na instância piloto | Observação |
|---|---|---|
| `WL__AfiliadaId` | `13` (AURUM) | O `TenantMiddleware` usa este valor como fonte de verdade e **ignora** o header `X-Tenant-AfiliadaId` enviado pelo frontend. |
| `WL__AllowedOrigins__0` | domínio do painel | Sem CORS o browser não consegue chamar o BFF — os frontends são CSR (ADR-WL-006). |
| `SeedAccount__Email` | `seed@aurumooh.com.br` | Conta de serviço, `UsuarioAfiliada` com `IdAfiliada = 13`. |
| `CoreApiUrl` | URL da API do core | Usada pelo `VeiculandoApiClient` para cadastrar local e peça. |
| `FILE_SERVER_URL` | URL do FileServer legado | Monta o link de PDF dos pedidos de inserção. |
| `WL_BRANDING_JSON` | JSON de branding | Opcional; consumido por `GET /api/wl/config/branding` antes do login. |

## A conta de serviço

Escrita de local e peça é delegada à API do core, autenticada como uma conta de
serviço — porque o `LocalCadastroHandler` é quem detém as regras de geração de
código, validação de afiliada e transição de aprovação.

A conta **precisa** ser um `UsuarioAfiliada`. É o tipo que faz o handler entrar
no ramo `EnviarParaAprovacao()`; com uma conta Admin, dois efeitos silenciosos:

1. locais do WhiteLabel nasceriam `Ativo`, **pulando a fila de aprovação**;
2. a guarda de tenant do core seria contornada — ela só roda dentro do
   `if (usuario is UsuarioAfiliada)`.

Requisitos da conta, todos verificados na criação:

- `StatusAprovacao = 1` (Aprovado) e `EmailConfirmado = 1` — `Usuario.Login()`
  recusa a autenticação sem os dois, e uma conta criada pelo fluxo normal nasce
  sem eles;
- permissão que satisfaça `VerificaPermissao(["PecaGerenciar", ...])`;
- hash de senha em **MD5 com pepper** (`Usuario.EncryptPassword`), **não**
  BCrypt — o BCrypt vale só para `WL_Usuario`, do painel.

## Rate limiting

| Escopo | Limite | Janela |
|---|---|---|
| `POST /api/wl/auth/login` | 10 req | 1 min por IP |
| Endpoints de escrita | 60 req | 1 min por IP |

Excedido, a resposta é **429**.

⚠️ O particionamento usa `RemoteIpAddress`. Atrás de proxy ou CDN esse endereço
passa a ser o do proxy, e o limite vira global em vez de por cliente — nesse
cenário, configurar `ForwardedHeaders` antes de confiar nos números.
