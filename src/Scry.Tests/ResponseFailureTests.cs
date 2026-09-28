/// <summary>
/// What a failure reported without a status line surfaces as — the error marker a hub answers with, the
/// event that closes a stream. The code carries the status the same failure would have been answered
/// with over HTTP, so a caller catching one never has to know which transport it came over.
/// </summary>
public class ResponseFailureTests
{
    [Test]
    [Arguments(ScryErrorCode.NotFound, HttpStatusCode.NotFound)]
    [Arguments(ScryErrorCode.PayloadTooLarge, HttpStatusCode.RequestEntityTooLarge)]
    [Arguments(ScryErrorCode.CommandLimit, HttpStatusCode.ServiceUnavailable)]
    [Arguments(ScryErrorCode.SubscriptionLimit, HttpStatusCode.ServiceUnavailable)]
    [Arguments(ScryErrorCode.Validation, HttpStatusCode.BadRequest)]
    [Arguments(ScryErrorCode.ExecutionFailed, HttpStatusCode.InternalServerError)]
    public async Task ACodeCarriesTheStatusItWouldHaveHad(ScryErrorCode code, HttpStatusCode status)
    {
        var body = ScryJson.SerializeToUtf8(
            new ScryError("Refused.")
            {
                Code = code
            });

        var exception = ResponseFailure.Read(body);

        await Assert.That(exception).IsTypeOf<ScryRequestException>();
        var request = (ScryRequestException) exception;
        using (Assert.Multiple())
        {
            await Assert.That(request.StatusCode).IsEqualTo(status);
            await Assert.That(request.Code).IsEqualTo(code);
        }
    }
}
