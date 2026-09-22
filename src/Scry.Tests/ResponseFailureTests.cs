/// <summary>
/// What a failure reported without a status line surfaces as — the error marker a hub answers with, the
/// event that closes a stream. The code carries the status the same failure would have been answered
/// with over HTTP, so a caller catching one never has to know which transport it came over.
/// </summary>
[TestFixture]
public class ResponseFailureTests
{
    [TestCase(ScryErrorCode.NotFound, HttpStatusCode.NotFound)]
    [TestCase(ScryErrorCode.PayloadTooLarge, HttpStatusCode.RequestEntityTooLarge)]
    [TestCase(ScryErrorCode.CommandLimit, HttpStatusCode.ServiceUnavailable)]
    [TestCase(ScryErrorCode.SubscriptionLimit, HttpStatusCode.ServiceUnavailable)]
    [TestCase(ScryErrorCode.Validation, HttpStatusCode.BadRequest)]
    [TestCase(ScryErrorCode.ExecutionFailed, HttpStatusCode.InternalServerError)]
    public void ACodeCarriesTheStatusItWouldHaveHad(ScryErrorCode code, HttpStatusCode status)
    {
        var body = ScryJson.SerializeToUtf8(
            new ScryError("Refused.")
            {
                Code = code
            });

        var exception = ResponseFailure.Read(body);

        Assert.That(exception, Is.TypeOf<ScryRequestException>());
        var request = (ScryRequestException) exception;
        Assert.Multiple(() =>
        {
            Assert.That(request.StatusCode, Is.EqualTo(status));
            Assert.That(request.Code, Is.EqualTo(code));
        });
    }
}
