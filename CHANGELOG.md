# Cambios

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
