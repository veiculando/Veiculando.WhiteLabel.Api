# Runbook — logs do preview (VEI-RD-102)

Como ler os logs de um serviço do preview (`veiculando-preview` na VM Azure)
sem acesso root, a partir de um `traceId` devolvido pelo BFF.

## 1. Pegar o traceId

Todo 5xx do BFF responde `application/problem+json`:

```json
{ "title": "Erro interno no servidor.", "status": 500, "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01" }
```

Toda resposta, de qualquer status, traz o mesmo valor no header `X-Trace-Id`.
Isso vale também para um 5xx repassado do Core com corpo próprio.

No log do container, a exceção sai numa linha única:

```
Erro nao tratado traceId=<id> metodo=GET rota=/api/wl/... afiliadaId=<n> tipo=<Exceção> mensagem=<...> stack=<...>
```

Mensagem e stack já saem mascaradas. Query string, Authorization e JWT nunca
são logados.

## 2. Disparar o workflow

```bash
gh workflow run preview-logs.yml -R veiculando/.github \
  -f servico=bff -f since=30m -f tail=500 -f trace=<traceId>
```

| input     | valores                                   | padrão |
|-----------|-------------------------------------------|--------|
| `servico` | `bff`, `exibidora`, `app`, `edge`, `core` | `bff`  |
| `since`   | `^[0-9]+[smh]$` (`90s`, `30m`, `2h`)      | `30m`  |
| `tail`    | 1 a 5000                                  | `500`  |
| `trace`   | opcional, `^[A-Za-z0-9-]+$`               | vazio  |

Um input inválido reprova o run antes do login no Azure, e a VM não é tocada.

## 3. Ler o resultado

```bash
run=$(gh run list -R veiculando/.github --workflow preview-logs.yml --limit 1 --json databaseId -q '.[0].databaseId')
gh run watch "$run" -R veiculando/.github --exit-status
gh run view "$run" -R veiculando/.github --log | grep -A200 'PREVIEW_LOGS_CONTAINER='
# ou o arquivo:
gh run download "$run" -R veiculando/.github -n preview-logs   # -> preview-logs.txt
```

Se houver outros disparos concorrentes, filtre por `--user` ou pelo horário
(`createdAt`) antes de pegar o `databaseId`.

## 4. Limites que o leitor precisa saber

- **~4 KB de saída.** O `az vm run-command` devolve só os últimos ~4 KB do
  stdout. O script imprime `PREVIEW_LOGS_LINES=` e `PREVIEW_LOGS_BYTES=` no
  fim. Se os bytes passarem de ~4000, o início foi cortado. Nesse caso, use
  `trace` ou reduza `since`/`tail`. Com `trace`, a busca é feita dentro das
  últimas `tail` linhas.
- **Sucesso = marcador.** O run só fica verde se a saída termina em
  `PREVIEW_LOGS=ok`. Um código `64` significa input inválido; `65` significa
  que não há container desse serviço no stack.
- **Somente leitura.** O script remoto é fixo
  (`veiculando/.github:.github/scripts/preview-logs-remote.sh`). Ele não abre o
  compose nem o `.env` do stack, não reinicia nada e não aceita comando. O
  container é achado pelos labels `com.docker.compose.*`.

## 5. Pré-requisito de infraestrutura (uma vez)

O workflow roda no environment `preview` do repo `veiculando/.github`, que
precisa de:

- os secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` e `AZURE_SUBSCRIPTION_ID`,
  da mesma identidade do deploy de preview;
- as vars `AZURE_RESOURCE_GROUP` e `AZURE_VM_NAME`;
- nessa identidade, um federated credential com subject
  `repo:veiculando/.github:environment:preview`.
