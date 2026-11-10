# FactuSmart — SDK de C# / .NET

SDK oficial del API de facturación electrónica de **FactuSmart** (SUNAT, Perú): emite facturas,
boletas, notas de crédito/débito y guías de remisión (GRE 2.0), anula facturas y boletas, calcula el
IGV y los totales, consulta los catálogos SUNAT, y descarga el PDF, el XML firmado y el CDR.

- `netstandard2.0` (.NET Framework 4.6.1+, .NET Core 2.0+) y `net8.0`. Usa `HttpClient`.
- **Sin dependencias externas**: en `net8.0` ninguna; en `netstandard2.0` solo `System.Text.Json` (de Microsoft).
- Todos los métodos son `async` y aceptan `CancellationToken`.
- Documentación del API: <https://factusmart.pe/api-facturacion-electronica/docs>

## Instalar

Pronto en NuGet. Mientras tanto, descarga `FactuSmart.0.1.0.nupkg` desde la release
[v0.1.0 en GitHub](https://github.com/factusmart/factusmart-dotnet/releases/tag/v0.1.0), guárdalo en una
carpeta (por ejemplo `C:\paquetes`) e instálalo desde ahí:

```bash
dotnet add package FactuSmart --version 0.1.0 --source C:\paquetes
```

`--source` es la carpeta donde guardaste el `.nupkg`, no el archivo.

## Ejemplo en 10 líneas

```csharp
using FactuSmart;

var fs = new FactuSmartClient(new FactuSmartOptions { ApiKey = "TU_API_KEY", BaseUrl = "https://TU_URL_BASE/api/v1/issuer", Ruc = "20100070970" });

var calc = FactuSmartClient.CalcularComprobante(new[] {
    new { descripcion = "Servicio de consultoría", cantidad = 1, valor_unitario = 100m, afectacion = "10" },
});

var r = await fs.Comprobantes.EmitirAsync(new {
    codigo_tipo_documento = "01",
    datos_del_cliente_o_receptor = new { codigo_tipo_documento_identidad = "6", numero_documento = "20131312955" },
    items = calc.Items, totales = calc.Totales,
});

Console.WriteLine($"{r.GetProperty("aceptado_por_sunat")} {r.GetProperty("serie_numero")}");
```

`BaseUrl` **no es igual para todas las cuentas**: cada cuenta vive en un servidor distinto. Cópiala
de tu panel de integrador (pestaña Configuración, "URL base de la API").

### Datos en español, tal cual el API

Los nombres de los métodos son PascalCase de .NET, pero en español como en los otros SDK
(`Comprobantes.EmitirAsync`, `ConsultarSunatAsync`…). Los **datos** no se traducen:

- **Envías** cualquier objeto que `System.Text.Json` sepa serializar con las claves del API: un objeto
  anónimo (`new { codigo_tipo_documento = "03" }`), un `Dictionary<string, object>`, un `JsonElement`
  o un `JsonObject`. Usa `decimal` (`12.50m`) para los montos.
- **Recibes** un `JsonElement` con la respuesta completa: `r.GetProperty("external_id").GetString()`.

## La regla de oro: HTTP 200 no significa "aceptado por SUNAT"

Un comprobante puede generarse bien y aun así no ser aceptado (SUNAT caída, rechazo, observación).
Eso **no lanza excepción**: el SDK devuelve la respuesta tal cual y **tú** debes revisar:

```csharp
if (!r.GetProperty("aceptado_por_sunat").GetBoolean())
{
    // ej. estado "01" → reintenta con fs.Comprobantes.ReenviarAsync(externalId)
    Console.WriteLine($"{r.GetProperty("estado")} {r.GetProperty("sunat")} {r.GetProperty("accion_requerida")}");
}
```

Revisa también `entorno` (`"prueba"` o `"produccion"`) y `tiene_validez_fiscal`: cada RUC nace en
modo prueba (SUNAT beta, sin validez fiscal).

## Opciones del cliente (`FactuSmartOptions`)

| Opción | Obligatoria | Default | Nota |
|---|---|---|---|
| `ApiKey` | sí | — | Va en el header `X-API-Key`. |
| `BaseUrl` | sí | — | URL base de tu servidor (sin barra final; si la pones, se quita). |
| `Ruc` | no | — | RUC emisor por defecto. Se cambia por llamada con `new OpcionesRuc { Ruc = "..." }`. |
| `Timeout` | no | 60 s | `TimeSpan` por petición (SUNAT a veces tarda). |
| `MaxReintentos` | no | `3` | Ver "Reintentos e idempotencia". |
| `VerificarSsl` | no | `true` | `false` **solo** para pruebas locales con certificado propio. |
| `HttpClient` | no | uno propio | Inyecta el tuyo (ej. de `IHttpClientFactory`). Con él, `VerificarSsl` se ignora: configúralo en tu handler. |

Si falta `ApiKey` o `BaseUrl`, el constructor lanza `FactuSmartException` con un mensaje claro.
`FactuSmartClient` es `IDisposable` (libera el `HttpClient` solo si lo creó él). Créalo **una vez**
y reúsalo (por ejemplo como singleton): es seguro usarlo desde varios hilos.

## Métodos

Todos devuelven `Task<JsonElement>` (salvo `DescargarAsync`, las consultas RUC/DNI y la configuración del RUC) y aceptan un `CancellationToken` al final.
`opciones` acepta `Ruc` (sobreescribe el del cliente) y, en `EmitirAsync`, `IdempotencyKey`.

| Método | Endpoint | Devuelve |
|---|---|---|
| `fs.PingAsync(opciones?)` | `GET /ping` | objeto |
| `fs.RaizAsync()` | `GET /` | objeto (`estado: "ok"`, `docs`) |
| `fs.ConsultarRucAsync(numero)` / `fs.ConsultarDniAsync(numero)` / `fs.ConsultarDocumentoAsync("ruc" \| "dni", numero)` | `GET /consultas/{tipo}/{numero}` | **`ConsultaDocumento`** |
| `fs.Cuenta.SaldoAsync()` | `GET /account` | objeto (`creditos_disponibles`, plan, RUCs) |
| `fs.Series.ListarAsync(opciones?)` | `GET /series` | objeto |
| `fs.Comprobantes.EmitirAsync(datos, opciones?)` | `POST /documents` | objeto |
| `fs.Comprobantes.ListarAsync(filtros?, opciones?)` | `GET /documents` | objeto (`comprobantes`, `siguiente_cursor`) |
| `fs.Comprobantes.ConsultarAsync(externalId, opciones?)` | `GET /documents/{id}` | objeto |
| `fs.Comprobantes.DescargarAsync(externalId, "pdf" \| "xml" \| "cdr", opciones?, formato: "ticket"?)` | `GET /documents/{id}/{tipo}?formato=` | **`byte[]`** |
| `fs.Comprobantes.ReenviarAsync(externalId, opciones?)` | `POST /documents/{id}/reenviar` | objeto |
| `fs.Comprobantes.ConsultarSunatAsync(externalId, opciones?)` | `POST /documents/{id}/consultar-sunat` | objeto |
| `fs.Comprobantes.HistorialAsync(externalId, opciones?)` | `GET /documents/{id}/historial` | objeto |
| `fs.Comprobantes.CalcularAsync(items, new OpcionesCalculo { PorcentajeIgv = 10.5m }?)` | `POST /calcular` | objeto (`items`, `totales`) |
| `fs.Anulaciones.CrearAsync(datos, opciones?)` | `POST /anulaciones` | objeto (`tipo`, `identificador`, `ticket`) |
| `fs.Anulaciones.EstadoAsync(ticket, opciones?)` | `GET /anulaciones/{ticket}/status` | objeto (`tipo`, `estado`, `documentos`) |
| `fs.Catalogos.ListarAsync(opciones?)` | `GET /catalogos` | objeto (`catalogos`, `nota`) |
| `fs.Catalogos.ConsultarAsync(nombre, new OpcionesCatalogo { Q, Ruc }?)` | `GET /catalogos/{nombre}` | objeto (`catalogo`, `total`, `datos`) |
| `fs.Catalogos.BuscarProductoSunatAsync(q, limite: 10?, nivel: "producto"?, opciones?)` | `GET /catalogos/productos-sunat` | objeto (`total`, `hay_mas`, `datos`) |
| `fs.Establecimientos.ListarAsync(opciones?)` | `GET /establecimientos` | objeto (`establecimientos`) |
| `fs.Establecimientos.CrearAsync(datos, opciones?)` | `POST /establecimientos` | objeto (`message`, `establecimiento`) |
| `fs.Guias.EmitirAsync(datos, opciones?)` | `POST /guias` | objeto (revisa `enviado`) |
| `fs.Guias.EstadoAsync(externalId, opciones?)` | `GET /guias/{id}/status` | objeto |
| `fs.Rucs.ListarAsync()` | `GET /rucs` | objeto |
| `fs.Rucs.CrearAsync(new { ruc, empresa, correo, clave })` | `POST /rucs` | objeto (`ya_existia`) |
| `fs.Rucs.ActivarAsync(ruc, activo)` | `PATCH /rucs/{ruc}/activation` | objeto |
| `fs.Rucs.SubirCertificadoAsync(ruc, rutaArchivo \| bytes \| stream, clave)` | `PUT /rucs/{ruc}/certificado` | **`CertificadoSubido`** |
| `fs.Rucs.ConfigurarSunatAsync(ruc, usuarioSol?, claveSol?, entorno?)` | `PUT /rucs/{ruc}/sunat` | **`ConfiguracionRuc`** |
| `fs.Rucs.VerificarAsync(ruc)` | `POST /rucs/{ruc}/verificar` | **`VerificacionRuc`** |
| `fs.Rucs.ConfiguracionAsync(ruc)` | `GET /rucs/{ruc}/configuracion` | **`ConfiguracionRuc`** |

Estáticos: `FactuSmartClient.CalcularComprobante(items)` y `FactuSmartClient.VerificarFirmaWebhook(cuerpo, firma, secreto)`.

### Lo que el SDK completa por ti

- **RUC:** en GET va como `?ruc=`; en POST va en el cuerpo como `ruc` (si ya lo pusiste en tus datos, se respeta el tuyo). Los endpoints `/rucs*` nunca lo reciben.
- **`numero_documento`:** si no lo mandas, va `"#"` (FactuSmart asigna el correlativo). Si lo mandas, se respeta (numeración propia).
- **`fecha_de_emision` / `hora_de_emision`:** si no las mandas, se usan la fecha y hora **de Perú** (UTC−5), no las de tu servidor.
- **`codigo_tipo_moneda`:** `"PEN"` si no viene. **`codigo_tipo_operacion`:** `"0101"` si no viene, solo en facturas y boletas (01/03); en notas no aplica.

### Descargar archivos

```csharp
File.WriteAllBytes("comprobante.pdf", await fs.Comprobantes.DescargarAsync(externalId, "pdf"));
```

El CDR solo existe cuando SUNAT respondió; si no existe, se lanza `NoEncontrado`.

**PDF en ticket u otro tamaño** (solo PDF): `a4`, `a5`, `ticket` (= `ticket80`, 80 mm), `ticket80`,
`ticket58`, `ticket50`. Sin `formato` sale el A4 de siempre.

```csharp
var ticket = await fs.Comprobantes.DescargarAsync(externalId, "pdf", formato: "ticket");   // 80 mm
var t58 = await fs.Comprobantes.DescargarAsync(externalId, "pdf", formato: "ticket58");    // 58 mm
```

Un `formato` con `"xml"`/`"cdr"` o fuera de la lista lanza `ErrorDeValidacion` (422, `Codigo`
`DATOS_INVALIDOS`, `Campos["formato"]`) **sin hacer la petición**. No cambia el PDF que recibe tu cliente.

### Anular facturas y boletas

`Anulaciones.CrearAsync` sirve para las dos; el API elige el camino según lo que mandes:

| Mandas | SUNAT lo anula con | `tipo` | `identificador` |
|---|---|---|---|
| Facturas y sus notas | Comunicación de baja | `baja` | `RA-AAAAMMDD-N` |
| Boletas y sus notas (solo **aceptadas**, estado `05`) | Resumen diario | `resumen` | `RC-AAAAMMDD-N` |
| Las dos cosas juntas | — | `422` `ANULACION_MIXTA` (`ErrorDeValidacion`) | — |

```csharp
var a = await fs.Anulaciones.CrearAsync(new {
    fecha_de_emision_de_documentos = "14-10-2026", // DD-MM-AAAA, la de emisión de los comprobantes
    documentos = new[] { new { external_id = externalId, motivo_anulacion = "Error en el comprobante" } },
});
var ticket = a.GetProperty("ticket").GetString();
var e = await fs.Anulaciones.EstadoAsync(ticket); // estado "05" = aceptada; documentos[i].estado "11" = anulado
```

La respuesta trae también `creditos_restantes` y `credito_cobrado` (y en RUCs demo `modo_demo` y
`pruebas_restantes_hoy`). Haz una petición para las facturas y otra para las boletas.
`Anulaciones.CrearAsync` nunca se reintenta automáticamente.

### Catálogos SUNAT

```csharp
var lista = await fs.Catalogos.ListarAsync(); // unidades, afectacion-igv, monedas, ...
var u = await fs.Catalogos.ConsultarAsync("unidades", new OpcionesCatalogo { Q = "kilo" }); // o por número: "03"
var activas = u.GetProperty("datos").EnumerateArray().Where(f => f.GetProperty("activo").GetBoolean());
```

Usa solo las filas activas: el RUC valida contra ellas. Un nombre que no existe lanza `NoEncontrado`
(la lista válida viene en `e.Respuesta`, propiedad `catalogos_disponibles`).

**Código de producto SUNAT** (catálogo 25), por texto (sin tildes) o por código exacto/prefijo:

```csharp
var r = await fs.Catalogos.BuscarProductoSunatAsync("computador", limite: 10, nivel: "producto"); // máx. 50; nivel: segmento|familia|clase|producto
foreach (var f in r.GetProperty("datos").EnumerateArray())
    Console.WriteLine($"{f.GetProperty("codigo")} {f.GetProperty("descripcion")}"); // 43211503 ...
// r.GetProperty("hay_mas") == true → afina la búsqueda
```

### Anexos (establecimientos)

```csharp
var l = await fs.Establecimientos.ListarAsync(); // codigo_anexo, nombre, series, predeterminado
var nuevo = await fs.Establecimientos.CrearAsync(new {
    codigo_anexo = "0001", nombre = "Tienda Miraflores", direccion = "Av. Larco 345", ubigeo = "150122",
    series = new[] { new { codigo_tipo_documento = "03", serie = "B002" } }, // opcional
});
```

- Registra antes el anexo en SUNAT (Clave SOL): el API no lo da de alta allí.
- `CrearAsync` **nunca se reintenta** (no es idempotente). Si el plan no permite más locales lanza
  `RucNoAutorizado` con `Codigo` `LIMITE_ESTABLECIMIENTOS`. No consumen crédito.
- Editar o eliminar un local se hace desde el sistema web.

### Consultar RUC o DNI

```csharp
var c = await fs.ConsultarRucAsync("20131312955"); // o fs.ConsultarDniAsync("12345678")
Console.WriteLine($"{c.Nombre} | {c.Direccion} | quedan {c.ConsultasRestantes} hasta {c.SeRenueva}");
```

- No emite, no gasta créditos y **no manda RUC emisor**. Repetir un número (30 días) responde con
  `DesdeCache = true` y no descuenta del cupo. `Estado`, `Condicion`, `Direccion` y `Ubigeo` pueden venir en null.
- Cupo agotado: `LimiteDePeticiones` con `Codigo` `LIMITE_CONSULTAS`, **sin reintentos**. No encontrado:
  `NoEncontrado`; número inválido: `ErrorDeValidacion`; `503 CONSULTA_NO_DISPONIBLE` se reintenta.

### Configurar un RUC (certificado, SOL y entorno)

Para dejar un RUC listo para emitir de verdad sin entrar al panel:

```csharp
// 1. Certificado digital (.pfx / .p12): el SDK lo lee y lo manda en base64.
var cert = await fs.Rucs.SubirCertificadoAsync("20777666555", @"C:\certs\empresa.pfx", claveDelPfx);
// también: SubirCertificadoAsync(ruc, byte[] bytes, clave) o SubirCertificadoAsync(ruc, Stream, clave)
Console.WriteLine($"{cert.Certificado.Titular} vence {cert.Certificado.Vence} (RUC coincide: {cert.Certificado.RucCoincide})");

// 2. Usuario secundario SOL + clave, y pasar a producción (manda solo lo que no es null).
var cfg = await fs.Rucs.ConfigurarSunatAsync("20777666555", usuarioSol: "FACTURA1", claveSol: claveSol, entorno: "produccion");

// 3. Prueba real contra SUNAT, sin emitir nada.
var v = await fs.Rucs.VerificarAsync("20777666555");
if (v.Checks.Sol.Ok == false) Console.WriteLine(v.Checks.Sol.Mensaje);

// 4. Ver cómo quedó (no llama a SUNAT).
var estado = await fs.Rucs.ConfiguracionAsync("20777666555");
Console.WriteLine($"{estado.Entorno} listo={estado.ListoParaProduccion} faltan={string.Join(",", estado.Faltantes)}");
```

- El RUC va en la ruta: no se manda el RUC emisor del cliente. No consumen crédito.
- `RucCoincide` y `Checks.*.Ok` pueden ser `null` (no se pudo comprobar o no aplica: en prueba,
  con OSE/PSE o si SUNAT no responde). `entorno` acepta `"prueba"` o `"produccion"`; `usuarioSol` y
  `claveSol` van juntos. La llave de prueba (`fs_test_…`) no puede subir certificados ni cambiar SOL o
  entorno (`RucNoAutorizado`, `Codigo` `SIN_PERMISO`).
- **La clave del certificado, la clave SOL y el contenido del certificado nunca aparecen en una
  excepción** (`Message`, `Mensaje`, `Respuesta`, `RespuestaTexto`, `ToString()`): si el servidor los
  repite, el SDK los cambia por `***`. El SDK no escribe logs.
- Archivo inexistente → `FactuSmartException` con la ruta (nunca la clave), sin petición.
- Las cuatro se reintentan (los `PUT` reemplazan lo anterior; verificar y consultar no tienen efectos).

## ¿Calcular en el servidor o local?

| | `fs.Comprobantes.CalcularAsync(items, opciones?)` | `FactuSmartClient.CalcularComprobante(items)` |
|---|---|---|
| Dónde | En FactuSmart (`POST /calcular`) | En tu proceso, sin petición |
| Precio | `valor_unitario` (sin IGV) **o** `precio_unitario` (con IGV) | Solo `valor_unitario` (sin IGV) |
| IGV | 18 % o 10.5 % (`PorcentajeIgv = 10.5m`, Ley 31556, restaurantes y hoteles MYPE acogidos) | Solo 18 % |
| Costo | No emite ni cobra crédito (ni manda `ruc`) | Cero peticiones |

Para lo que soportan los dos, dan **los mismos números** al centavo.

- Usa `CalcularAsync` si tus precios incluyen IGV, si aplicas la tasa 10.5 % o si prefieres que el
  cálculo lo haga siempre FactuSmart. Con `precio_unitario` se respeta lo que paga el cliente:
  3 menús de S/ 10.00 suman S/ 30.00 (no S/ 29.99).
- Usa `CalcularComprobante` si tus precios son sin IGV al 18 % y no quieres una petición extra.

```csharp
var c = await fs.Comprobantes.CalcularAsync(new[] {
    new { descripcion = "Menú", cantidad = 3, precio_unitario = 10m, afectacion = "10", unidad_de_medida = "ZZ" },
});
await fs.Comprobantes.EmitirAsync(new {
    codigo_tipo_documento = "03", datos_del_cliente_o_receptor = cliente,
    items = c.GetProperty("items"), totales = c.GetProperty("totales"),
});
```

Los errores de `CalcularAsync` vienen por ítem en `e.Campos` (clave `items.N.campo`). Al no tener
efectos, se reintenta igual que un GET.

## `CalcularComprobante(items)`

Recibe ítems (objeto anónimo, `Dictionary` o `JsonElement`) con `descripcion`, `cantidad`,
`valor_unitario` (**sin IGV**), `afectacion` y opcionalmente `codigo_interno` y `unidad_de_medida`
(default `NIU`). Devuelve un `ComprobanteCalculado` con `Items` y `Totales` listos para emitir
(si lo serializas entero sale `{ "items": [...], "totales": {...} }`).

| `afectacion` | Significado | IGV | Suma en |
|---|---|---|---|
| `10` | Gravado | 18% | `total_operaciones_gravadas` |
| `20` | Exonerado | 0 | `total_operaciones_exoneradas` |
| `30` | Inafecto | 0 | `total_operaciones_inafectas` |
| `40` | Exportación | 0 | `total_exportacion` |

Redondeo a 2 decimales por ítem, "mitad hacia arriba" (`MidpointRounding.AwayFromZero`), con `decimal`:
`10.555 × 3` da `31.67`, igual que en los SDK de PHP, Node y Python. Un dato inválido lanza `ArgumentException`.

## Errores

Todos heredan de `FactuSmartException` y traen:

- `Status`: HTTP (`0` si fue red/timeout).
- `Mensaje`: el `message` del API. `Respuesta`: el cuerpo completo (`JsonElement?`); `RespuestaTexto`: el cuerpo crudo.
- `Codigo`: código estable del error, en MAYÚSCULAS (ej. `DATOS_INVALIDOS`). Úsalo en tu lógica en
  vez de comparar textos. `null` si fue red/timeout o si el servidor es de una versión anterior.
- `Reintentable`: `true` si repetir la misma petición más tarde puede funcionar. `null` si el API no lo mandó.
- `Campos`: errores por campo, ej. `{ "items.0.cantidad": ["..."] }` (sobre todo en 422). Vacío si no hay.

| HTTP | Clase | Cuándo |
|---|---|---|
| 401 | `ErrorDeAutenticacion` | API key ausente, inválida o cuenta desactivada |
| 402 | `SinCreditos` | Sin créditos; no se emitió nada |
| 403 | `RucNoAutorizado` | El RUC no es de tu cuenta o está desactivado |
| 404 | `NoEncontrado` | Comprobante, guía, ticket o archivo inexistente |
| 422 | `ErrorDeValidacion` | Datos inválidos; el crédito no se cobra. Revisa `Campos` |
| 429 | `LimiteDePeticiones` | Límite de peticiones; `SegundosDeEspera` si se pudo leer |
| otros / red | `FactuSmartException` | |

```csharp
try
{
    await fs.Comprobantes.EmitirAsync(datos);
}
catch (ErrorDeValidacion e) { Console.WriteLine($"Corrige el JSON: {e.Mensaje}"); }
catch (SinCreditos) { Console.WriteLine("Sube de plan o pide créditos extra para tu plan"); }
catch (FactuSmartException e) when (e.Codigo == "PLAN_VENCIDO") { Console.WriteLine("Renueva tu plan"); }
```

Códigos más comunes (lista en `FactuSmartException.CodigosDeError`; el API puede agregar otros y manda
`ERROR_<status>` como genérico, así que no asumas una lista cerrada):

| `Codigo` | Qué pasó |
|---|---|
| `NO_AUTENTICADO` | API key inválida o cuenta desactivada |
| `PLAN_VENCIDO` / `SIN_CREDITOS` | Plan vencido o sin créditos |
| `RUC_NO_AUTORIZADO` / `RUC_DESACTIVADO` | El RUC no es de tu cuenta o está desactivado |
| `NO_ENCONTRADO` | Comprobante, guía, ticket o archivo inexistente |
| `DATOS_INVALIDOS` | Validación; detalle en `Campos` |
| `EMISION_EN_CURSO` | Ya hay una emisión con esa `Idempotency-Key` en proceso |
| `RECHAZADO_SUNAT` / `ERROR_EMISION` | SUNAT rechazó o falló la emisión |
| `SUNAT_NO_DISPONIBLE` | SUNAT no responde (reintentable) |
| `LIMITE_PETICIONES` | Demasiadas peticiones por minuto (reintentable) |
| `LIMITE_PRUEBAS_DIARIO` | 429 **no reintentable**: tope de 200 operaciones/día en RUCs demo |
| `LIMITE_CONSULTAS` | 429 **no reintentable**: cupo de consultas RUC/DNI agotado (se renueva en `se_renueva`) |
| `CONSULTA_NO_DISPONIBLE` | 503: SUNAT y el respaldo no responden (reintentable) |
| `ANULACION_MIXTA` | Facturas y boletas en la misma anulación (422) |
| `LLAVE_DE_PRUEBA_EN_PRODUCCION` | Llave de prueba usada con un RUC en producción (403) |
| `ERROR_INTERNO` | Error del servidor |

Si cancelas con tu `CancellationToken`, se lanza `OperationCanceledException` (no `FactuSmartException`).
Un timeout del SDK (`Timeout`) sí es `FactuSmartException` con `Status = 0`.

### RUCs demo: sin costo, con tope diario

En un RUC en modo demo (pruebas, SUNAT beta, sin validez fiscal) **no se cobra crédito**: las respuestas
traen `credito_cobrado: false`, `modo_demo: true` y `pruebas_restantes_hoy`, y `creditos_restantes` no
baja. A cambio hay un tope de **200 operaciones por día por cuenta**; al llegar, el API responde 429
con `codigo: LIMITE_PRUEBAS_DIARIO` y `reintentable: false`. El SDK lanza `LimiteDePeticiones` tras
**una sola** petición (no reintenta: el cupo se reinicia a medianoche, hora de Perú). Distínguelo del
límite por minuto con `e.Codigo`, no con la clase:

```csharp
catch (LimiteDePeticiones e) when (e.Codigo == "LIMITE_PRUEBAS_DIARIO") { Console.WriteLine("Se acabaron las pruebas de hoy"); }
```

## Verificar webhooks

FactuSmart firma cada webhook con el header `X-FactuSmart-Firma`: HMAC-SHA256 (hex) del cuerpo
**crudo** con tu secreto. Verifica con el cuerpo tal como llegó, **nunca** con un JSON re-serializado:
cambia espacios u orden y la firma ya no coincide.

```csharp
// ASP.NET Core (minimal API)
app.MapPost("/webhooks/factusmart", async (HttpRequest req) =>
{
    using var lector = new StreamReader(req.Body);
    var cuerpo = await lector.ReadToEndAsync();
    if (!FactuSmartClient.VerificarFirmaWebhook(cuerpo, req.Headers["X-FactuSmart-Firma"].ToString(), secreto))
        return Results.Unauthorized();
    var evento = JsonDocument.Parse(cuerpo).RootElement;
    // ... procesa evento
    return Results.Ok();
});
```

`VerificarFirmaWebhook(cuerpoCrudo, firma, secreto)` acepta `string` o `byte[]`, compara en tiempo
constante y nunca lanza: si la firma o el secreto faltan o están mal formados, devuelve `false`.

## Reintentos e idempotencia

- `Comprobantes.EmitirAsync` **siempre** manda el header `Idempotency-Key`: la tuya
  (`new OpcionesEmision { IdempotencyKey = "venta-123" }`, recomendado: el id de la venta en tu sistema)
  o un UUID v4 generado. **La misma clave se reusa en todos los reintentos de esa llamada**, así un
  reintento nunca emite dos veces ni cobra dos veces.
- Se reintenta solo ante: error de red/timeout, 429, 502, 503, 504 y, en `EmitirAsync`, 409
  ("emisión en curso con esa clave"). Espera creciente: 1 s, 2 s, 4 s… (máx. 60 s). Ante límite de
  peticiones se respeta `Retry-After` o el "Espera N segundos" del mensaje.
- Se reintentan: todos los GET, `EmitirAsync`, `CalcularAsync` (no tiene efectos), `Rucs.CrearAsync`
  (idempotente por RUC), `Rucs.SubirCertificadoAsync`, `Rucs.ConfigurarSunatAsync` (los dos reemplazan
  lo anterior) y `Rucs.VerificarAsync` (no emite). En estas operaciones también se reintenta cuando el API responde
  `reintentable: true` (ej. `ERROR_INTERNO`, `SUNAT_NO_DISPONIBLE`), con el mismo tope y la misma espera.
- **`reintentable: false` manda:** si el cuerpo del error trae `reintentable: false`, no se reintenta
  nunca, aunque el HTTP sea 429, 409, 502, 503 o 504 (caso real: 429 `LIMITE_PRUEBAS_DIARIO`). Si el
  cuerpo no trae `reintentable` (servidor de versión anterior), se usa la regla por HTTP de arriba.
- **Nunca** se reintentan automáticamente: `Anulaciones.CrearAsync`, `Guias.EmitirAsync` (no acepta
  `Idempotency-Key`), `ReenviarAsync`, `ConsultarSunatAsync`, `Rucs.ActivarAsync`, aunque el API
  responda `reintentable: true`.

## Desarrollo del SDK

```bash
dotnet build
dotnet test --filter "Categoria!=Integracion&Categoria!=IntegracionLectura"   # sin red (handler falso) + contrato, en net8.0 y net48
dotnet test -f net8.0 --filter "Categoria=IntegracionLectura"                 # API local, solo lectura (no emite ni crea nada)
dotnet test                                     # todo, incluido el API local https://factusmart.test (máx. 3 emisiones demo)
dotnet pack src/FactuSmart -c Release -o artifacts
```

Las pruebas del API local usan `FACTUSMART_BASE_URL`, `FACTUSMART_API_KEY`, `FACTUSMART_RUC` y
`FACTUSMART_OPENAPI` (ruta del `openapi.yaml`; por defecto `tests/fixtures/openapi.yaml`) si están definidas.
`resultado_caso_comun.json` se regenera con `FACTUSMART_GENERAR_CASO_COMUN=1`.

## Licencia

MIT
