using System.Text.Json;
using System.Text.Json.Serialization;

namespace FactuSmart
{
    /// <summary>
    /// Respuesta de <c>GET /consultas/{tipo}/{numero}</c> (<c>LookupResponse</c>): datos de un RUC o DNI.
    /// Los campos que la fuente no trae vienen en null (SUNAT directo no trae <c>estado</c> ni <c>condicion</c>;
    /// del DNI solo viene <c>nombre</c>).
    /// </summary>
    public sealed class ConsultaDocumento
    {
        [JsonPropertyName("success")] public bool Success { get; set; }

        /// <summary><c>ruc</c> o <c>dni</c>.</summary>
        [JsonPropertyName("tipo")] public string Tipo { get; set; } = "";

        /// <summary>El número consultado.</summary>
        [JsonPropertyName("numero")] public string Numero { get; set; } = "";

        /// <summary>RUC: razón social. DNI: nombres completos.</summary>
        [JsonPropertyName("nombre")] public string Nombre { get; set; } = "";

        /// <summary>Domicilio fiscal del RUC. null si la fuente no lo trae (DNI casi siempre).</summary>
        [JsonPropertyName("direccion")] public string? Direccion { get; set; }

        /// <summary>Estado del contribuyente (ej. ACTIVO). null cuando responde SUNAT directo.</summary>
        [JsonPropertyName("estado")] public string? Estado { get; set; }

        /// <summary>Condición del domicilio (ej. HABIDO). null cuando responde SUNAT directo.</summary>
        [JsonPropertyName("condicion")] public string? Condicion { get; set; }

        /// <summary>Código INEI del distrito (6 dígitos).</summary>
        [JsonPropertyName("ubigeo")] public string? Ubigeo { get; set; }

        /// <summary>true = respondido desde la caché de 30 días; no descontó del cupo.</summary>
        [JsonPropertyName("desde_cache")] public bool DesdeCache { get; set; }

        /// <summary>Consultas que te quedan en el periodo actual.</summary>
        [JsonPropertyName("consultas_restantes")] public int ConsultasRestantes { get; set; }

        /// <summary><c>mes</c> = cuenta con plan; <c>dia</c> = cuenta sin plan (50 por día).</summary>
        [JsonPropertyName("periodo")] public string Periodo { get; set; } = "";

        /// <summary>Día (yyyy-MM-dd) en que el cupo vuelve a empezar.</summary>
        [JsonPropertyName("se_renueva")] public string SeRenueva { get; set; } = "";

        /// <summary>JSON completo tal como llegó (por si el API agrega campos nuevos).</summary>
        [JsonIgnore] public JsonElement Respuesta { get; set; }
    }

    /// <summary>Base de las respuestas tipadas de la configuración del RUC: trae el JSON crudo en <see cref="Respuesta"/>.</summary>
    public abstract class RespuestaTipada
    {
        [JsonPropertyName("success")] public bool Success { get; set; }

        /// <summary>JSON completo tal como llegó (por si el API agrega campos nuevos).</summary>
        [JsonIgnore] public JsonElement Respuesta { get; set; }
    }

    /// <summary>Respuesta de <c>PUT /rucs/{ruc}/certificado</c> (<c>UploadCertificateResponse</c>). Nunca trae la clave ni el certificado.</summary>
    public sealed class CertificadoSubido : RespuestaTipada
    {
        [JsonPropertyName("ruc")] public string Ruc { get; set; } = "";
        [JsonPropertyName("certificado")] public CertificadoInstalado Certificado { get; set; } = new CertificadoInstalado();

        /// <summary>Solo si <c>Certificado.RucCoincide</c> es null (el titular no trae RUC).</summary>
        [JsonPropertyName("aviso")] public string? Aviso { get; set; }
    }

    /// <summary>Certificado recién instalado.</summary>
    public sealed class CertificadoInstalado
    {
        [JsonPropertyName("instalado")] public bool Instalado { get; set; }

        /// <summary>CN (u O) del titular.</summary>
        [JsonPropertyName("titular")] public string? Titular { get; set; }

        /// <summary>Fecha de vencimiento (yyyy-MM-dd).</summary>
        [JsonPropertyName("vence")] public string? Vence { get; set; }
        [JsonPropertyName("dias_para_vencer")] public int? DiasParaVencer { get; set; }

        /// <summary>true = el RUC del titular es el de la ruta; null = el titular no trae RUC (no se pudo comprobar).</summary>
        [JsonPropertyName("ruc_coincide")] public bool? RucCoincide { get; set; }
    }

    /// <summary>
    /// Respuesta de <c>GET /rucs/{ruc}/configuracion</c> y <c>PUT /rucs/{ruc}/sunat</c> (<c>RucConfigResponse</c>).
    /// Nunca trae claves.
    /// </summary>
    public sealed class ConfiguracionRuc : RespuestaTipada
    {
        [JsonPropertyName("ruc")] public string Ruc { get; set; } = "";
        [JsonPropertyName("nombre")] public string? Nombre { get; set; }

        /// <summary><c>prueba</c> o <c>produccion</c>.</summary>
        [JsonPropertyName("entorno")] public string Entorno { get; set; } = "";
        [JsonPropertyName("certificado")] public EstadoCertificado Certificado { get; set; } = new EstadoCertificado();
        [JsonPropertyName("sol")] public EstadoSol Sol { get; set; } = new EstadoSol();

        /// <summary><c>sunat</c>, <c>ose</c> o <c>pse</c>.</summary>
        [JsonPropertyName("envio")] public string Envio { get; set; } = "";
        /// <summary><c>propio</c> (SUNAT directo u OSE) o <c>pse</c> (firma Smart PSE). null en servidores anteriores.</summary>
        [JsonPropertyName("modalidad")] public string? Modalidad { get; set; }

        /// <summary>Solo con <c>Modalidad</c> <c>pse</c>; null en otro caso.</summary>
        [JsonPropertyName("pse")] public EstadoPse? Pse { get; set; }

        [JsonPropertyName("listo_para_produccion")] public bool ListoParaProduccion { get; set; }

        /// <summary>
        /// <c>certificado</c>, <c>certificado_vencido</c>, <c>certificado_sin_clave_privada</c>, <c>certificado_otro_ruc</c>
        /// (el certificado instalado no es de este RUC), <c>sol</c> (también con modalidad pse en producción),
        /// <c>pse_credenciales</c>. Ver las constantes de <see cref="Faltante"/>.
        /// </summary>
        [JsonPropertyName("faltantes")] public string[] Faltantes { get; set; } = new string[0];

        /// <summary>
        /// Opcional. En PUT /sunat y en GET /configuracion: recordatorio de designar a Smart PSE como PSE en SUNAT
        /// (modalidad pse), certificado que vence en menos de 30 días («El certificado vence el AAAA-MM-DD.»),
        /// credenciales PSE que no se pudieron comprobar en prueba, o el cambio confirmado de producción a prueba.
        /// </summary>
        [JsonPropertyName("aviso")] public string? Aviso { get; set; }
    }

    /// <summary>Valores de <see cref="ConfiguracionRuc.Faltantes"/>. La lista no es cerrada.</summary>
    public static class Faltante
    {
        public const string Certificado = "certificado";
        public const string CertificadoVencido = "certificado_vencido";
        public const string CertificadoSinClavePrivada = "certificado_sin_clave_privada";
        /// <summary>El certificado instalado no es de este RUC (solo modalidad propio).</summary>
        public const string CertificadoOtroRuc = "certificado_otro_ruc";
        /// <summary>Usuario SOL; también obligatorio con modalidad pse en producción.</summary>
        public const string Sol = "sol";
        public const string PseCredenciales = "pse_credenciales";
    }

    /// <summary>Estado del certificado del RUC.</summary>
    public sealed class EstadoCertificado
    {
        [JsonPropertyName("instalado")] public bool Instalado { get; set; }
        [JsonPropertyName("vence")] public string? Vence { get; set; }

        /// <summary>Negativo si ya venció.</summary>
        [JsonPropertyName("dias_para_vencer")] public int? DiasParaVencer { get; set; }
    }

    /// <summary>Estado del usuario SOL del RUC.</summary>
    public sealed class EstadoSol
    {
        /// <summary>Hay usuario secundario y clave SOL propios (MODDATOS no cuenta).</summary>
        [JsonPropertyName("configurado")] public bool Configurado { get; set; }

        /// <summary>Usuario enmascarado (<c>FA*****1</c>), <c>MODDATOS</c> en prueba, o null.</summary>
        [JsonPropertyName("usuario")] public string? Usuario { get; set; }
    }

    /// <summary>Estado del PSE del RUC (solo con modalidad <c>pse</c>). La clave nunca se devuelve.</summary>
    public sealed class EstadoPse
    {
        /// <summary><c>smartpse</c> u <c>otro</c>.</summary>
        [JsonPropertyName("proveedor")] public string Proveedor { get; set; } = "";

        /// <summary>Hay usuario y clave PSE guardados.</summary>
        [JsonPropertyName("configurado")] public bool Configurado { get; set; }

        /// <summary>Usuario PSE enmascarado (<c>FU*****F</c>) o null.</summary>
        [JsonPropertyName("usuario")] public string? Usuario { get; set; }
    }

    /// <summary>Respuesta de <c>POST /rucs/{ruc}/verificar</c> (<c>VerifyRucConfigResponse</c>).</summary>
    public sealed class VerificacionRuc : RespuestaTipada
    {
        [JsonPropertyName("ruc")] public string Ruc { get; set; } = "";
        [JsonPropertyName("listo_para_emitir")] public bool ListoParaEmitir { get; set; }
        [JsonPropertyName("entorno")] public string Entorno { get; set; } = "";

        /// <summary><c>propio</c> o <c>pse</c>. null en servidores anteriores.</summary>
        [JsonPropertyName("modalidad")] public string? Modalidad { get; set; }
        [JsonPropertyName("checks")] public ComprobacionesRuc Checks { get; set; } = new ComprobacionesRuc();
    }

    /// <summary>Comprobaciones de <see cref="VerificacionRuc"/>.</summary>
    public sealed class ComprobacionesRuc
    {
        [JsonPropertyName("certificado")] public Comprobacion Certificado { get; set; } = new Comprobacion();
        [JsonPropertyName("sol")] public Comprobacion Sol { get; set; } = new Comprobacion();

        /// <summary>Solo con modalidad <c>pse</c>: token de prueba a Smart PSE (no emite). null en otro caso.</summary>
        [JsonPropertyName("pse")] public Comprobacion? Pse { get; set; }
    }

    /// <summary>Una comprobación (<c>ConfigCheck</c>).</summary>
    public sealed class Comprobacion
    {
        /// <summary>true correcto · false hay un problema · null no se pudo comprobar o no aplica.</summary>
        [JsonPropertyName("ok")] public bool? Ok { get; set; }
        [JsonPropertyName("mensaje")] public string Mensaje { get; set; } = "";
    }
}
