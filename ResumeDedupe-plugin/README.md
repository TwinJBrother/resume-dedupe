# Resume Dedupe 1.3.1 (Jellyfin 12.x, ABI 12.0.0.0; carrega em 12.0 e superiores)

Plugin 100% server-side (uma DLL na sua pasta de plugins). Nao mexe na interface web
nem na imagem base compartilhada da seedbox. Funciona em qualquer cliente que use a API
padrao do Jellyfin: Moonfin (todas as plataformas), Web, Android TV, Swiftfin, Findroid...

## O que faz
- Continuar Assistindo: um episodio por serie (o mais recente). Configuravel (1 a 10).
- Next Up: esconde series que ja aparecem em Continuar Assistindo. No Moonfin, que junta
  as duas listas numa linha so, cada serie aparece uma vez.
- Respeita a paginacao do Moonfin (startIndex/limit): as paginas nao repetem nem pulam itens
  e a linha continua cheia depois de remover duplicatas.
- Trabalha com objetos (nao com bytes da resposta): sem problema com gzip/brotli/proxy da seedbox.
- Se der qualquer erro, a resposta original do Jellyfin passa sem alteracao.
- Custo zero fora dos dois endpoints: filtro unico (singleton) que devolve o pedido sem
  alocar nada quando nao e Resume/NextUp. Dentro deles, uma consulta leve por usuario, em cache de 2 s.
- Nao mexe em Continue Listening/Reading (mediaTypes sem Video), so filmes ou busca (searchTerm).
- Escolhe o episodio mais recente tambem pela data de reproducao (UserData.LastPlayedDate), nao so pela ordem da lista.
- So altera pedidos do proprio usuario do token (admin vendo outra conta passa sem alteracao).

## Compilar sem instalar nada
1. Crie um repositorio no GitHub e envie o conteudo do zip (mantenha a pasta .github).
2. Actions > build > Run workflow. Baixe o artefato ResumeDedupe_1.3.1.0.
   Se a compilacao falhar, copie a mensagem de erro do log e envie para ajuste.

## Instalar
1. Painel do Jellyfin: veja o caminho da pasta de configuracao/dados (Painel > Geral/Dashboard).
2. Copie a pasta ResumeDedupe_1.3.1.0 (DLL + meta.json) para <config>/plugins/ .
3. Reinicie o Jellyfin (reinicie o app slot no painel da seedbox).
4. Painel > Plugins > Resume Dedupe: ajuste as opcoes.

## Verificar se esta funcionando
No log do Jellyfin procure "ResumeDedupe". Ao primeiro uso real aparecem as linhas
"Continue Watching deduplicado ... Funcionando." e "Next Up filtrado ... Funcionando.".
Se aparecer "tipo de resposta inesperado" ou "nao foi possivel alterar a resposta", me envie a linha.

## Limites conhecidos
- Linhas do plugin Home Screen Sections (/HomeScreen/Section/...) nao sao filtradas (so os endpoints padrao).
- Nao use junto com outro plugin que tambem filtre Continue Watching/Next Up.
- Se voce esconder (Hide) no Moonfin o episodio mais recente de uma serie, os outros
  episodios dela ja foram removidos pelo plugin e a serie some da linha.
