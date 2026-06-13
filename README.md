# Trailer Cinema — Plugin para Jellyfin

Reproduce automáticamente trailers en castellano antes de cada película, a modo de cine.

## Instalación

1. En Jellyfin: **Dashboard → Plugins → Catálogo → ⚙️ Repositorios**
2. Añade el repositorio:
   ```
   https://raw.githubusercontent.com/War563/jellyfin-plugin-trailer-cinema/main/manifest.json
   ```
3. Busca **Trailer Cinema** en el catálogo e instálalo
4. Reinicia Jellyfin
5. Ve a **Dashboard → Plugins → Trailer Cinema** y configura:
   - ID del canal de YouTube
   - API Key de YouTube Data v3
   - Número de trailers, filtro de título, etc.

## Requisitos

- Jellyfin 10.8+
- [`yt-dlp`](https://github.com/yt-dlp/yt-dlp) instalado y en el PATH del servidor
- API Key de [YouTube Data API v3](https://console.cloud.google.com/)

## Funcionamiento

1. Al arrancar Jellyfin se descarga un pool de trailers del canal configurado
2. Se resuelven las URLs de stream directas con `yt-dlp` (sin descargar el vídeo)
3. Al pulsar Play en una película, los trailers se reproducen automáticamente antes
4. El pool se renueva cada 6 horas

## Desarrollo

```bash
# Compilar
dotnet build JellyfinTrailerPlugin/JellyfinTrailerPlugin.csproj

# Publicar una versión (crea release automáticamente via GitHub Actions)
git tag v1.0.0
git push origin v1.0.0
```
