using System.Globalization;
using System.Runtime.CompilerServices;

namespace Veiculando.WhiteLabel.Api.Tests.Infrastructure;

// VEI-RD-171h: o container do BFF roda sem LANG, isto e, com a cultura invariante, e
// o model binding de formulario le numeros pela cultura atual. Numa maquina pt-BR,
// "-23.55" no multipart virava 400 de Latitude. Os testes rodam na cultura do container.
internal static class CulturaDosTestes
{
    [ModuleInitializer]
    internal static void Fixar()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}
