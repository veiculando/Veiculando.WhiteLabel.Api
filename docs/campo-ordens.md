# Ordens do aplicativo de campo — VEI-RD-154b

As rotas exigem a policy `Campo`. O operador vem da claim `UsuarioOperadorId`,
e a afiliada vem do Host resolvido no servidor. Nem operador nem afiliada são
parâmetros de consulta. Token de outra afiliada retorna 401. Ordem inexistente,
Rascunho, de outra afiliada ou sem atribuição/histórico do operador retorna 404.

## GET /api/wl/campo/ordens

Query: `busca`, `estado`, `de`, `ate`, `ordenacao=recente|antigo|proximo`,
`lat`, `lng`, `pagina` (1 por padrão), `tamanho` (25 por padrão, máximo 100).
Datas filtram ordens cujo período cruza o intervalo solicitado.
Proximidade exige as duas coordenadas. Filtros inválidos retornam 400.

Resposta: `{ itens, total, pagina, tamanho }`. A consulta, os filtros e a
ordenação usam `ListaCampoConsulta` do Core no SQL, antes do OFFSET/LIMIT.
Ordens em que ainda existe ação do operador vêm primeiro. O ID desempata
resultados com mesma data/distância para estabilizar a paginação.

Cada item contém `id`, `numeroFormatado`, `papeis`, `cidade`, `periodo`
(`id`, `nome`, `de`, `ate`), `endereco`, `estado`, `progresso`
(`tipo=colagens|fotos`, `feitos`, `total`), `acao` e `dataInicio`.
Papéis são `colador`, `fiscal` ou `somente_historico`; o responsável legado
recebe os dois papéis, conforme as regras do Core. Atribuição atual prevalece
sobre histórico. Ações são `Iniciar`, `Abrir` ou `Consulta`. Somente o colador
atual recebe `Iniciar`; estados finais e somente histórico recebem `Consulta`.

## GET /api/wl/campo/ordens/{id}

Mesmo cabeçalho da lista, acrescido de `locais`. Antes de `dataInicio`, a
coleção é vazia. Depois do início, os locais trazem `id`, `numero`, `codigo`,
`logradouro`, `bairro`, `cidade` e `pecas`. Peças trazem `id`, `codigo`,
`anterior`, `atual` (nomes das campanhas), `motivo`, `dataColagem`,
`dataFotoCampo`, `latitude`, `longitude`, `fotoAnteriorUrl` e `arteAtualUrl`.
Instantes preservam segundos e offset. A ordem permanece consultável depois
de desatribuição, cancelamento ou conclusão, respeitando a visibilidade.

Campanha anterior usa o período imediatamente anterior da mesma periodicidade,
como no PDF da Exibidora. As campanhas são resolvidas por PedidoInsercao,
PedidoReserva ou Pedido do PecaPeriodoStatus. Foto anterior é o checking mais
recente da peça em período anterior, dentro da afiliada. A arte usa a campanha
atual e o formato da peça; havendo várias artes ativas compatíveis, a mais
recente (data/ID) é escolhida. O motivo é o da arte selecionada.

As imagens retornam URLs relativas autenticadas em
`/api/wl/campo/imagens/checking/{id}` e `/api/wl/campo/imagens/arte/{id}`.
Sem arquivo compatível, o valor é null. Nomes internos de arquivo, blobs e
credenciais não são enviados. O streaming dessas URLs pertence à subtarefa
154d. As rotas GET não aplicam migrations nem alteram ordens.

## Ações (154c)

- `POST /api/wl/campo/ordens/{id}/iniciar`
- `POST /api/wl/campo/ordens/{id}/pecas/{idPeca}/colagem`

Corpo: `{ idCliente: uuid, hora: DateTimeOffset, latitude, longitude }`.
Coordenadas são obrigatórias para iniciar e opcionais na colagem. A ordem
precisa ser visível ao operador (mesmo filtro do GET); caso contrário, 404.

O `idCliente` gerado no aparelho é a chave de `OrdemServicoAcaoCampo` e é
gravado na mesma transação da ordem pelo serviço `AcoesCampo` do Core. Repetir
o mesmo id devolve o resultado já gravado, sem reaplicar: 200
`{ idCliente, idPeca, hora, ordem }`, com `ordem` no formato da lista, ou o
mesmo 409. Em corrida, a PK recusa a segunda gravação e o registro é relido.

Recusas do Core saem como 409 `{ codigo, message }` (`campo.somente_historico`,
`campo.ordem_cancelada`, `campo.fora_de_pendente` etc.). Um `idCliente` já usado
em outra ação, ordem, peça ou operador sai como 409
`campo.id_cliente_reutilizado`.
