using System;
using Veiculando.Domain.Entities;

namespace Veiculando.WhiteLabel.Api.Middleware
{
    public static class TenantInvariantExtensions
    {
        public static void AssertTenantAccess(this EntityDefBase entity, int tenantAfiliadaId)
        {
            if (entity == null)
                return;

            var property = entity.GetType().GetProperty("AfiliadaId") ?? entity.GetType().GetProperty("IdAfiliada");
            if (property != null)
            {
                var entityTenantId = (int)property.GetValue(entity);
                if (entityTenantId != tenantAfiliadaId)
                {
                    throw new UnauthorizedAccessException("Acesso negado: Tentativa de manipulação de recurso de outro tenant.");
                }
            }
        }
    }
}
