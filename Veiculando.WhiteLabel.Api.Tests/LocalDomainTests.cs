using System;
using FluentAssertions;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Enums;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

public class LocalDomainTests
{
    [Fact]
    public void Local_tem_transicao_de_estado_centralizada()
    {
        // Reflexão permite executar RED antes da introdução do contrato no Core.
        var transition = typeof(Local).GetMethod("TentarAlterarStatusWhiteLabel");
        transition.Should().NotBeNull();
        var local = (Local)Activator.CreateInstance(typeof(Local), nonPublic: true)!;
        bool Alterar(StatusExibicaoEnum status) => (bool)transition!.Invoke(local, new object[] { status })!;
        Alterar(StatusExibicaoEnum.Deletado).Should().BeFalse();
        Alterar(StatusExibicaoEnum.Inativo).Should().BeTrue();
        Alterar(StatusExibicaoEnum.Ativo).Should().BeFalse();
        Alterar(StatusExibicaoEnum.AprovacaoPendente).Should().BeTrue();
        Alterar(StatusExibicaoEnum.Inativo).Should().BeFalse();
        Alterar(StatusExibicaoEnum.Deletado).Should().BeTrue();
        Alterar(StatusExibicaoEnum.AprovacaoPendente).Should().BeFalse();
    }
}
