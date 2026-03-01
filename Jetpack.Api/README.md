# Jetpack Plugin Server

A simple JetBrains plugin server backed by Minio S3 storage.

## Endpoints

### Upload Plugin

Upload a plugin zip file. The server extracts metadata from `META-INF/plugin.xml` inside the zip.

**URL:** `POST /api/plugins/upload`
**Content-Type:** `application/zip` (or binary)

**Curl Command:**
```bash
curl -X POST "http://localhost:5000/api/plugins/upload" \
     --data-binary "@path/to/your-plugin.zip" \
     -H "Content-Type: application/zip"
```

### Get Update Plugins XML

Returns the `updatePlugins.xml` containing metadata for all uploaded plugins. Configure your IDE to use this URL for custom plugin repositories.

**URL:** `GET /api/plugins/updatePlugins.xml`

**Curl Command:**
```bash
curl "http://localhost:5000/api/plugins/updatePlugins.xml"
```

### Download Plugin

Downloads a specific plugin zip file.

**URL:** `GET /api/plugins/download/{fileName}`

**Curl Command:**
```bash
curl -O "http://localhost:5000/api/plugins/download/plugin-id-1.0.0.zip"
```

## Configuration

You can configure Minio settings using environment variables or `appsettings.json`. Environment variables take precedence.

### Environment Variables

*   `MINIO_ENDPOINT`: The Minio server endpoint (e.g., `play.min.io`).
*   `MINIO_ACCESS_KEY`: The access key for Minio.
*   `MINIO_SECRET_KEY`: The secret key for Minio.
*   `MINIO_SECURE`: Set to `true` for HTTPS, `false` for HTTP (default: `false`).
*   `MINIO_PLUGINS_BUCKET`: The bucket name for storing plugin zip files.
*   `MINIO_METADATA_BUCKET`: The bucket name for storing plugin metadata XML files.

### appsettings.json

```json
"Minio": {
  "Endpoint": "play.min.io",
  "AccessKey": "minioadmin",
  "SecretKey": "minioadmin",
  "Secure": true,
  "PluginsBucket": "plugins-storage",
  "MetadataBucket": "plugins-metadata"
}
```
