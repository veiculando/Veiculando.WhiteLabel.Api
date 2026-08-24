using System;
using System.Reflection;

namespace Veiculando.WhiteLabel.Api.Tests.TestHelpers
{
    /// <summary>
    /// As entidades do Core (EntityBase/EntityDefBase) só expõem setters
    /// privados e não têm construtores triviais para popular grafos de teste
    /// isolados de EF/SQL Server. `Type.GetProperty(name).SetValue(...)` falha
    /// silenciosamente em propriedades com setter privado DECLARADO NA CLASSE
    /// BASE (ex.: Id em EntityDefBase, acessado a partir de Peca) — o
    /// binding flag padrão não inclui membros não-públicos herdados. Este
    /// helper sobe a hierarquia até achar a propriedade declarada.
    /// </summary>
    internal static class ReflectionTestHelpers
    {
        public static void SetPrivate(object target, string propertyName, object value)
        {
            var type = target.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            while (type != null)
            {
                var property = type.GetProperty(propertyName, flags);
                if (property != null)
                {
                    property.SetValue(target, value);
                    return;
                }
                type = type.BaseType;
            }

            throw new InvalidOperationException($"Propriedade '{propertyName}' não encontrada em {target.GetType().FullName} nem em suas classes base.");
        }

        public static T CreateUninitialized<T>() =>
            (T)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(T));
    }
}
