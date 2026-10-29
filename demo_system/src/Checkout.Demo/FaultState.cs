namespace Checkout.Demo;

internal enum CheckoutFault { Healthy, Slow, VerySlow, Errors }

internal sealed class FaultState
{
    private int _mode;

    public CheckoutFault Mode => (CheckoutFault)Volatile.Read(ref _mode);

    public bool TrySet(string? mode)
    {
        CheckoutFault? next = mode switch
        {
            "healthy" => CheckoutFault.Healthy,
            "slow" => CheckoutFault.Slow,
            "very-slow" => CheckoutFault.VerySlow,
            "errors" => CheckoutFault.Errors,
            _ => null
        };
        if (next is null) return false;
        Volatile.Write(ref _mode, (int)next.Value);
        return true;
    }
}
