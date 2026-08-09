namespace EngrCAD.Core.Solvers;

/// <summary>
/// A preconditioner for a Krylov solver — a cheap stand-in for A⁻¹ that clusters the
/// spectrum so the iteration converges in far fewer steps. <see cref="Apply"/> computes
/// <c>z = M⁻¹·r</c> for the implicit operator M ≈ A; the solvers never form M or M⁻¹, they
/// only apply it.
/// </summary>
/// <remarks>
/// Consumed by the non-symmetric solvers (<see cref="Gmres"/>, <see cref="BiCgStab"/>) and,
/// via <see cref="CgOptions.Preconditioner"/>, by preconditioned conjugate gradients. Each
/// solver treats a <c>null</c> preconditioner as the identity, so an unpreconditioned solve
/// costs no apply at all rather than an apply that copies. The one concrete implementation
/// today is <see cref="Ilu0"/>; the interface exists so a future block/multigrid
/// preconditioner drops in without touching the solvers.
/// </remarks>
public interface IPreconditioner
{
    /// <summary>Dimension of the operator this preconditions.</summary>
    int Rows { get; }

    /// <summary>
    /// Writes <c>z = M⁻¹·r</c>. Both spans must have length <see cref="Rows"/>; they may
    /// alias each other only if the implementation documents it (<see cref="Ilu0"/> does
    /// not — pass distinct buffers).
    /// </summary>
    void Apply(ReadOnlySpan<double> r, Span<double> z);
}
