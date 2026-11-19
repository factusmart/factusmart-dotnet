# Cambios

## 0.7.0 — 2026-11-19

Reenvío de guías a SUNAT y código de error para notas de crédito que exceden el comprobante.

- `Guias.ReenviarAsync(externalId, opciones?, cancellationToken?)` → `POST /guias/{external_id}/reenviar`.
  Reintenta el envío de una guía que quedó en `01` (Registrado). Cobra 1 crédito solo si sale bien; la respuesta
  trae `serie_numero`, `enviado`, `estado`, `mensaje`, `links`, `envio` y el resumen del cobro. Envío fallido o
  guía que no está en `01` → `ErrorDeValidacion` (422, `REENVIO_FALLIDO`; si falló el envío, `reintentable: true`
  y no se cobra; si es por estado, trae `estado`). Inexistente → `NoEncontrado`. No se reintenta automáticamente.
- `FactuSmartException.CodigosDeError` suma `NOTA_EXCEDE_COMPROBANTE` (422, no reintentable): la nota de crédito,
  sumada a las notas vivas del comprobante afectado, pasaría su total.
- Prueba de contrato con `resendDispatchGuide` (33 operationId); fixture `tests/fixtures/openapi.yaml` actualizada.
- Instrucciones de instalación apuntan a `FactuSmart.0.7.0.nupkg` (release v0.7.0).

```csharp
var r = await fs.Guias.ReenviarAsync("a1b2c3");   // r.GetProperty("enviado").GetBoolean() == true
```

## 0.6.0 — 2026-11-18

Descarga de archivos de guías y anulaciones, y `links` en las respuestas.

- `Guias.DescargarAsync(externalId, "pdf" | "xml" | "cdr", opciones?, cancellationToken?)` →
  `GET /guias/{external_id}/{tipo}`; devuelve `byte[]`. El CDR de una guía llega como XML.
- `Anulaciones.DescargarAsync(ticket, "xml" | "cdr", opciones?, cancellationToken?)` →
  `GET /anulaciones/{ticket}/{tipo}`; devuelve `byte[]` (el CDR es un ZIP). Sin PDF: `"pdf"` lanza
  `FactuSmartException` sin hacer la petición.
- Archivo inexistente o CDR aún no disponible → `NoEncontrado` (404, `NO_ENCONTRADO`). Son GET: se reintentan
  como cualquier GET.
- Las respuestas traen `links`: comprobantes (`pdf`, `pdf_ticket`, `xml`, `cdr`), guías (`xml`, `pdf`, `cdr`;
  en `EmitirAsync` dentro de `envio`) y anulaciones (`xml`, `cdr`). Llegan tal cual en el `JsonElement`.
- Prueba de contrato con `downloadDispatchGuideFile` y `downloadVoidedFile` (32 operationId); fixture
  `tests/fixtures/openapi.yaml` actualizada.
- Instrucciones de instalación apuntan a `FactuSmart.0.6.0.nupkg` (release v0.6.0).

## 0.5.0 — 2026-11-10

Configuración del RUC por API con certificado propio o Smart PSE (contrato nuevo del backend).

- `Rucs.ConfigurarSunatAsync` gana `bool? confirmar = null` (antes del token): se manda `"confirmar": true`
  solo si es `true`. Obligatorio para pasar un RUC de producción a prueba. La firma de la 0.4.0
  `(ruc, usuarioSol, claveSol, entorno, modalidad, pseUsuario, pseClave, cancellationToken)` y la de la
  0.3.0 se conservan como sobrecargas.
- Códigos nuevos en `FactuSmartException.CodigosDeError`: `LIMITE_VERIFICACIONES` (429, reintentable tras
  Retry-After), `CONFIRMAR_CAMBIO_ENTORNO` (422, no reintentable) y `PSE_NO_DISPONIBLE` (503, reintentable).
  `RUC_DESACTIVADO` (403) también en certificado, sunat y verificar.
- 429 `LIMITE_VERIFICACIONES` → `LimiteDePeticiones` con `SegundosDeEspera` (Retry-After). El SDK no lo
  reintenta solo (la espera puede ser de hasta una hora).
- Nueva clase `Faltante` con los valores de `ConfiguracionRuc.Faltantes`, incluido `certificado_otro_ruc`.
  `ConfiguracionRuc.Aviso` también llega en `GET /configuracion`.
- XML-doc y README: modalidad `propio` (certificado + SOL) vs `pse` (Smart PSE, sin certificado, con SOL en
  producción) y designar a Smart PSE en SUNAT.
- Instrucciones de instalación apuntan a `FactuSmart.0.5.0.nupkg` (release v0.5.0).
- Fixture `tests/fixtures/openapi.yaml` actualizada.

## 0.4.0 — 2026-11-10

- `Rucs.ConfigurarSunatAsync` acepta `modalidad` (`propio` | `pse`), `pseUsuario` y `pseClave`
  (credenciales CPE de Smart PSE, van juntas) → `modalidad`, `pse_usuario`, `pse_clave` en
  `PUT /rucs/{ruc}/sunat`. Van después de `entorno` y antes del token; la firma de la 0.3.0
  `(ruc, usuarioSol, claveSol, entorno, cancellationToken)` se conserva como sobrecarga.
  `modalidad: "pse"` sin credenciales se manda tal cual (el API lo permite sobre un RUC ya en PSE).
- `entorno` acepta `demo` (sinónimo de `prueba` en el API); antes el SDK lo rechazaba. Nuevo `RecursoRucs.Modalidades`.
- `pseClave` se reemplaza por `***` en cualquier excepción, igual que la clave SOL.
- Modelos: `ConfiguracionRuc.Modalidad`, `ConfiguracionRuc.Pse` (`EstadoPse`: `Proveedor`, `Configurado`,
  `Usuario`; null si no es PSE), `VerificacionRuc.Modalidad` y `Checks.Pse` (null si no es PSE).
- **Cambio de comportamiento:** `PUT /rucs/{ruc}/sunat` (`ConfigurarSunatAsync`) y `PUT /rucs/{ruc}/certificado`
  (`SubirCertificadoAsync`) ya **no se reintentan** automáticamente (cambian la configuración), igual que en los otros SDKs.
- Fixture `tests/fixtures/openapi.yaml` actualizada.

## 0.3.0 — 2026-11-10

- Configuración del RUC por API (tag «Configuración del RUC»):
  - `Rucs.SubirCertificadoAsync(ruc, rutaArchivo | byte[] | Stream, clave)` → `PUT /rucs/{ruc}/certificado`
    (`uploadRucCertificate`). El SDK lee el .pfx / .p12 y lo manda en base64 (`{ certificado, clave }`).
    Archivo inexistente → `FactuSmartException` con la ruta, sin la clave y sin petición.
  - `Rucs.ConfigurarSunatAsync(ruc, usuarioSol?, claveSol?, entorno?)` → `PUT /rucs/{ruc}/sunat`
    (`updateRucSunat`). Solo manda los no nulos (`usuario_sol`, `clave_sol`, `entorno`); valida
    localmente `prueba`/`produccion` y que usuario y clave SOL vayan juntos.
  - `Rucs.VerificarAsync(ruc)` → `POST /rucs/{ruc}/verificar` (`verifyRucConfig`), sin cuerpo.
  - `Rucs.ConfiguracionAsync(ruc)` → `GET /rucs/{ruc}/configuracion` (`getRucConfig`).
  - Modelos tipados `CertificadoSubido`, `ConfiguracionRuc` y `VerificacionRuc` (con el JSON crudo en `Respuesta`).
  - El RUC va solo en la ruta (ni `ruc` en el cuerpo/query ni header). Las cuatro se reintentan.
- Seguridad: la clave del certificado, la clave SOL y el certificado en base64 se reemplazan por `***`
  en `Message`, `Mensaje`, `Respuesta`, `RespuestaTexto` y `Campos` de cualquier excepción, aunque el
  servidor los repita (también en su forma escapada de JSON/PHP). Pruebas con un 422 que hace eco.
- Fixture `tests/fixtures/openapi.yaml` actualizada (30 operationId).

## 0.2.0 — 2026-11-06

- Consultas de RUC y DNI (`GET /consultas/{tipo}/{numero}`, operationId `lookupDocument`):
  `ConsultarRucAsync(numero)`, `ConsultarDniAsync(numero)` y `ConsultarDocumentoAsync(tipo, numero)`.
  Devuelven el modelo tipado `ConsultaDocumento` (`Nombre`, `Direccion`, `Estado`, `Condicion`, `Ubigeo`
  —estos cuatro pueden ser null—, `DesdeCache`, `ConsultasRestantes`, `Periodo`, `SeRenueva` y el JSON
  crudo en `Respuesta`). No mandan RUC emisor (ni `?ruc=` ni header). Se reintentan como un GET.
- `RaizAsync()` (`GET /`, operationId `apiRoot`): comprueba que el API responde.
- Códigos `LIMITE_CONSULTAS` (429, no reintentable) y `CONSULTA_NO_DISPONIBLE` (503, reintentable)
  en `CodigosDeError`. Prueba de que el 429 `LIMITE_CONSULTAS` lanza tras una sola petición.
- Fixture `tests/fixtures/openapi.yaml` actualizada (26 operationId).

## 0.1.0 — 2026-10-14

- Primera versión (sin publicar en NuGet). Paridad con los SDK de Node 1.1.0, PHP 0.2.0 y Python 0.2.0:
  - `PingAsync`, `Cuenta.SaldoAsync`, `Series.ListarAsync`.
  - `Comprobantes`: `EmitirAsync` (siempre con `Idempotency-Key`), `ListarAsync`, `ConsultarAsync`,
    `DescargarAsync` (`byte[]`), `ReenviarAsync`, `ConsultarSunatAsync`, `HistorialAsync`, `CalcularAsync`.
  - `Anulaciones.CrearAsync/EstadoAsync` (facturas y boletas), `Guias.EmitirAsync/EstadoAsync`,
    `Rucs.ListarAsync/CrearAsync/ActivarAsync`, `Catalogos.ListarAsync/ConsultarAsync`.
  - `Comprobantes.DescargarAsync(id, "pdf", formato: "ticket")`: PDF en `a4`, `a5`, `ticket`/`ticket80`,
    `ticket58` o `ticket50` (`?formato=`; sin formato no se manda). Formato con xml/cdr o inválido →
    `ErrorDeValidacion` local (422, `DATOS_INVALIDOS`, `Campos["formato"]`) sin petición.
  - `Establecimientos.ListarAsync` (`GET /establecimientos`, se reintenta) y `Establecimientos.CrearAsync`
    (`POST /establecimientos`, `ruc` en el cuerpo, nunca se reintenta).
  - `Catalogos.BuscarProductoSunatAsync(q, limite)` (`GET /catalogos/productos-sunat`, catálogo 25).
  - Códigos `PDF_NO_GENERADO`, `LIMITE_ESTABLECIMIENTOS` y `CATALOGO_NO_DISPONIBLE` en `CodigosDeError`.
  - Estáticos `FactuSmartClient.CalcularComprobante(items)` (decimal, mismo resultado al centavo que
    los otros SDK) y `FactuSmartClient.VerificarFirmaWebhook(cuerpo, firma, secreto)`.
  - Errores `FactuSmartException` (`Status`, `Mensaje`, `Codigo`, `Reintentable`, `Campos`, `Respuesta`)
    y subclases `ErrorDeAutenticacion`, `SinCreditos`, `RucNoAutorizado`, `NoEncontrado`,
    `ErrorDeValidacion`, `LimiteDePeticiones`.
  - Reintentos con espera creciente; `reintentable: false` manda (429 `LIMITE_PRUEBAS_DIARIO` no se reintenta).
- Targets `netstandard2.0` (.NET Framework 4.6.1+, .NET Core 2.0+, Unity, Xamarin) y `net8.0`.
  Única dependencia: `System.Text.Json` (paquete de Microsoft), solo en `netstandard2.0`.
