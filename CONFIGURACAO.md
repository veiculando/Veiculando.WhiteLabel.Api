# Configuração — Veiculando.WhiteLabel.Api

O BFF é **um deploy só para todos os tenants** (ADR-WL-012). O tenant sai do
`Host` da requisição, resolvido contra `WlDominio`. Não há uma instância por
cliente, nem variável que fixe a afiliada.

**Valores nunca ficam neste repositório.** Em preview e em produção, cada chave
vem do Snaps: environment `default (preview)` ou `default (production)`,
materializado na VM por bilhete OIDC (ADR-WL-017). O nome de cada chave no Snaps
e o mapa para a variável do container estão no compose de cada ambiente, no
repositório do Core:

- produção: `deploy/production/docker-compose.yml`, com contrato em
  `deploy/production/stack.env.example` (só nomes);
- preview: `deploy/preview/docker-compose.yml`.

> O separador é **duplo sublinhado** (`__`), que o provider de configuração do
> ASP.NET Core traduz para o `:` da hierarquia do JSON.

## Obrigatórias na subida

Sem estas, a aplicação **não sobe**, e a falha diz o que falta.

| Variável | Para quê | Chave no Snaps (produção) |
|---|---|---|
| `ConnectionStrings__Veiculando` | Banco do Core. | `CORE_CONNECTION_STRING` |
| `JwtSettings__Secret` | Assinatura HMAC-SHA256 dos JWTs do WL. Mínimo de 32 caracteres. **Distinto** do JWT do Core (ADR-WL-008). | `WL_JWT_SECRET` |

### Por que o `JwtSettings__Secret` não tem default

O `appsettings.json` versionado trazia
`"WlCustomSecretKey_ChangeInProd_NeedsToBeAtLeast32Chars12345"`. Com um segredo
público no repositório, qualquer pessoa com acesso ao código **forja um JWT de
operador válido**, sem precisar de credencial nenhuma e sem deixar rastro de
login. Por isso o campo é vazio e a subida falha se ele não vier do ambiente.
Gere um valor por ambiente (`openssl rand -base64 48`) direto no Snaps.

## Obrigatórias por uso

A aplicação sobe sem elas, mas a funcionalidade que depende de cada uma falha
fechada (erro explícito, nunca default silencioso).

| Variável | Para quê | Chave no Snaps (produção) |
|---|---|---|
| `SeedAccounts__{AfiliadaId}__Email` | Conta de serviço **daquele tenant**, que escreve no Core. Sem ela, a escrita de local e peça do tenant falha com "Conta de servico nao configurada para a afiliada N". | `WL_CORE_SERVICE_EMAIL` (AUR = 13) |
| `SeedAccounts__{AfiliadaId}__Password` | Senha dessa conta (MD5 com pepper do Core, não BCrypt). | `WL_CORE_SERVICE_PASSWORD` |
| `CoreApiUrl` | API do Core. Em produção, `http://veiculando-api/` pela rede interna `veiculando-prd-core`. | fixo no compose |
| `FileServerUrl` | FileServer, para o PDF de PI. Em produção, `http://veiculando-fs-api/`. Nunca chega ao browser. O nome antigo `FILE_SERVER_URL` ainda é lido. | fixo no compose |
| `ConnectionStrings__BlobStorageConnStr` | Storage **próprio** de uploads do WL (HTTPS), nunca a conexão de arquivos do Core. | `WL_UPLOADS_CONNECTION_STRING` |
| `WlUploads__Container` | Container de uploads. Precisa começar com `wl-uploads-` e ser **privado**; o BFF recusa container público. | `WL_UPLOADS_CONTAINER` (`wl-uploads-prd`) |
| `WlPasswordEmail__SendGridApiKey` | Convite, primeiro acesso e recuperação de senha. | `SENDGRID_API_KEY` |
| `WlPasswordEmail__FromEmail` | Remetente autenticado no SendGrid. | `WL_PASSWORD_FROM_EMAIL` |
| `WlProspeccao__AppUrl` | Origem HTTPS do App para o handoff de prospecção. Sem ela, `POST /api/wl/prospeccao/sessao` responde 503. | `https://` + `WL_APP_HOST` |
| `GoogleMapsApiKey` | Chave de **navegador** do Maps, entregue pelo mapa-config (restrita por referrer). Nunca a chave servidor-a-servidor do Core. | `GOOGLE_MAPS_BROWSER_API_KEY` |
| `ReverseProxy__KnownProxies__N` | IPs literais dos proxies confiáveis. Ver "Atrás do proxy". | fixo no compose |

A conta de um tenant novo entra como `SeedAccounts__<AfiliadaId>__*`, no compose e
no Snaps. Nada mais muda no BFF.

## A conta de serviço

Escrita de local e peça é delegada à API do Core, autenticada como a conta de
serviço do tenant, porque o `LocalCadastroHandler` é quem detém as regras de
geração de código, validação de afiliada e transição de aprovação.

A conta **precisa** ser um `UsuarioAfiliada` da afiliada do tenant. É o tipo que
faz o handler entrar no ramo `EnviarParaAprovacao()`. Com uma conta Admin, dois
efeitos silenciosos:

1. locais do WhiteLabel nasceriam `Ativo`, **pulando a fila de aprovação**;
2. a guarda de tenant do Core seria contornada, porque ela só roda dentro do
   `if (usuario is UsuarioAfiliada)`.

Requisitos da conta:

- `StatusAprovacao = 1` (Aprovado) e `EmailConfirmado = 1`. `Usuario.Login()`
  recusa a autenticação sem os dois, e uma conta criada pelo fluxo normal nasce
  sem eles;
- as permissões `PecaGerenciar`, `Checking`, `PedidoReservaGerenciar` e
  `PedidoInsercaoGerenciar`. Em produção, quem acrescenta o que falta é o
  provisionador versionado do Core (`veiculando-migrator --provision-tenant`),
  que nunca lê nem troca a senha;
- hash de senha em **MD5 com pepper** (`Usuario.EncryptPassword`), **não**
  BCrypt. O BCrypt vale só para `WL_Usuario`, do painel.

## Atrás do proxy

Em produção a cadeia é **Cloudflare → cloudflared → edge (nginx) → BFF**
(ADR-WL-018). O edge acrescenta o IP do cloudflared ao `X-Forwarded-For`, então o
BFF recebe `"<cliente>, <cloudflared>"` vindo do IP do edge.

- `ReverseProxy__KnownProxies__0` e `__1` listam o edge e o cloudflared (IPs
  fixos no compose: 172.30.0.10 e 172.30.0.11 em produção; 172.29.0.10 e
  172.29.0.11 no preview).
- O BFF desembrulha **um salto por proxy configurado** (`ForwardLimit` = número
  de `KnownProxies`). Com um proxy só, ou nenhum, fica o padrão do ASP.NET Core
  (1).
- Sem o IP do cloudflared na lista, o cliente visto vira o cloudflared e o rate
  limit por IP passa a ser **global**. Um `X-Forwarded-For` forjado à esquerda
  fica além do limite e não escolhe a partição (`ProxyReversoAtrasDoTunnelTests`).

## Rate limiting

| Escopo | Limite | Janela |
|---|---|---|
| `POST /api/wl/auth/login` | 10 req | 1 min por IP |
| Endpoints de escrita | 60 req | 1 min por IP |
| Esqueci-senha e alterar-senha | 5 req | 1 min por Host + IP |

Excedido, a resposta é **429**. O particionamento usa `RemoteIpAddress`, que só
é o do cliente com `KnownProxies` correto (seção anterior).

# Hotfix — primeiro acesso e links públicos

O administrador cria operadores sem senha. `POST /api/wl/usuarios` persiste o
operador pendente e tenta enviar o convite. Em falha do provedor, responde 503
com `id`, `conviteEnviado=false` e mensagem explícita; a conta permanece sem
senha, para `POST /api/wl/usuarios/{id}/reenviar-convite`. Este endpoint exige
`UsuarioAfiliadaGerenciar`, revoga o token anterior e recusa contas já ativadas
com 409. Falhas de envio invalidam somente o token daquela tentativa.

`POST /api/wl/auth/primeiro-acesso` consome o convite por Host/e-mail/hash e cria
a senha. A rowversion do EF6 protege aceite versus reenvio concorrente. Não
retorna JWT. Convite e recuperação são independentes.

- `WlPasswordEmail__ConviteValidadeHoras`: 1–168 horas (padrão 48).
- Links usam HTTPS e o Host cadastrado em `WlDominio` por padrão.
- `WlPublicOrigins__Hosts__<host>` permite explicitar origem/porta do **mesmo**
  Host. Não permite caminho, query, fragmento, credenciais ou outro domínio.
- HTTP só é aceito com `ASPNETCORE_ENVIRONMENT=Preview` **e**
  `WlPublicOrigins__AllowHttpPreview=true`. Nunca habilitar para usuários/dados
  reais: HTTP expõe senha/token em trânsito. Preferir HTTPS no preview.
- Exemplo de origem para dados sintéticos:
  `WlPublicOrigins__Hosts__exibidora-preview.20.42.92.186.nip.io=http://exibidora-preview.20.42.92.186.nip.io:9080`.

Esta configuração não migra o banco. Aplicar a migration EF6 do Core antes de
subir o BFF; `core.ref` fixa o SHA compatível para o CI.

# Módulo CMS (Aurum) — `api/wl/cms/*`

Desligado por padrão. Só liga numa afiliada quando as **duas** chaves estão
presentes **e** o código da afiliada (`Afiliada.Codigo`) está na lista. Fora
disso, todo `api/wl/cms/*` responde 404 (antes da autorização, então sem 401 ou 403),
e o branding traz `cmsHabilitado: false` e `cmsSiteUrl: null`.

| Variável | Origem no preview (Snaps) | Observação |
|---|---|---|
| `Cms__SupabaseUrl` | `CMS_SUPABASE_URL` | Barra final é removida. |
| `Cms__ServiceRoleKey` | `CMS_SUPABASE_SERVICE_ROLE_KEY` | Escrita irrestrita no Supabase que a LP de **produção** lê. Nunca versionar, logar ou expor. |
| `Cms__SiteUrl` | `CMS_SITE_URL` | URL pública da LP, exposta no branding como `cmsSiteUrl`. Precisa ser `https` absoluta, senão vira null (com Warning). Barra final é removida. |
| `Cms__AfiliadasHabilitadas__0` | `CMS_AFILIADAS_HABILITADAS` | Código da afiliada (ex.: `PRVIEW`). Aceita `A,B` num único item. Comparação sem diferenciar maiúsculas. |

Por que a lista existe: o BFF é compartilhado entre as exibidoras e o tenant sai
do Host. Com as chaves no ambiente e `ConteudoGerenciar` concedido a todo admin,
um flag só pelas chaves deixaria o admin de outra exibidora gravar no site da
Aurum (ADR-CMS-004).
