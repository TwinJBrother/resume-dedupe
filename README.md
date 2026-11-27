Resume Dedupe 3.0.3 (Jellyfin 12.x)
Plugin server-side (uma DLL). Um episodio por serie em Continuar Assistindo e o Next Up
sem as series que ja estao em andamento. Funciona em Moonfin, Wholphin, AFinity e qualquer
cliente que use /UserItems/Resume e /Shows/NextUp. Sem tela de configuracao.
Instalar pelo proprio Jellyfin (repositorio de plugins)
O repositorio do GitHub precisa ser PUBLICO. Cada build gera um Release com o zip e o manifest.json.
Painel > Plugins > Repositorios > adicionar:
`https://github.com/TwinJBrother/resume-dedupe/releases/latest/download/manifest.json`
Painel > Plugins > Catalogo > Resume Dedupe > Instalar.
Reinicie o Jellyfin (na seedbox, reinicie o app slot).
Se houver uma instalacao manual antiga, desinstale-a (ou apague a pasta em `<config>/plugins/`) antes.
Verificar
Log do Jellyfin, procure `ResumeDedupe`: ao primeiro uso aparecem
"Resume filtrado ... Funcionando." e "NextUp filtrado ... Funcionando.".
Avisos: "fora da ordem de reproducao" (o plugin se adapta sozinho e le a lista inteira), "resposta inesperada",
"nao foi possivel consultar", "pausado por 10 minutos" -> envie a linha.
Limites
Nao filtra linhas do plugin Home Screen Sections.
Episodio abandonado pela metade continua em Continuar Assistindo e tira a serie do Next Up.
Esconder (Hide) no cliente o episodio mais recente de uma serie faz a serie sumir da linha.
Nao use com outro plugin que tambem filtre Continue Watching / Next Up.
