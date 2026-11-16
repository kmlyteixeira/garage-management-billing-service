using Xunit;

public sealed class PaymentContractTests
{
    [Fact]
    public void Rejected_payment_is_not_approved()
    {
        var payment = new PaymentResponse(Guid.NewGuid(), "rejected", null);
        Assert.NotEqual("approved", payment.Status);
    }
}
