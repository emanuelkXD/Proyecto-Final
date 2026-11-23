namespace CatalogoApi.Dtos;

/// <summary>
/// DTO principal del reporte autorizado de comunidad de Marco Antonio Arias Giron.
/// Combina las tablas resenas, listas_reproduccion, favoritos y premios.
/// </summary>
public class ReporteComunidadMarcoDto
{
    public string Autor { get; set; } = "Marco Antonio Arias Giron";
    public List<string> Tablas { get; set; } = new() { "resenas", "listas_reproduccion", "favoritos", "premios" };
    public string Proposito { get; set; } = string.Empty;
    public DateTime GeneradoEn { get; set; }
    public ReporteComunidadResumenDto Resumen { get; set; } = new();
    public List<ReporteObraComunidadDto> DesglosePorObra { get; set; } = new();
    public List<ReporteListaResumenDto> ListasReproduccion { get; set; } = new();
}

/// <summary>
/// Totales globales de actividad de comunidad y listas activas.
/// </summary>
public class ReporteComunidadResumenDto
{
    public int TotalResenas { get; set; }
    public double PromedioPuntuacionResenas { get; set; }
    public int TotalFavoritos { get; set; }
    public int TotalPremios { get; set; }
    public int TotalListasReproduccion { get; set; }
    public int ListasPublicas { get; set; }
}

/// <summary>
/// Métricas combinadas por obra cruzando reseñas, favoritos y premios.
/// </summary>
public class ReporteObraComunidadDto
{
    public string TituloObra { get; set; } = string.Empty;
    public int CantidadResenas { get; set; }
    public double PromedioPuntuacion { get; set; }
    public int CantidadFavoritos { get; set; }
    public int CantidadPremios { get; set; }
    public List<string> Premios { get; set; } = new();
}

/// <summary>
/// Resumen de listas de reproducción activas incluidas en el reporte.
/// </summary>
public class ReporteListaResumenDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string Propietario { get; set; } = string.Empty;
    public bool EsPublica { get; set; }
    public string? Descripcion { get; set; }
}
