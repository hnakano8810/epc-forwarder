namespace EpcForwarder.Core.Tests.Epc;

internal static class GtinTestData
{
    // body13 = indicator + 12 digits; appends the GS1 mod-10 check digit
    public static string Gtin14(string body13)
    {
        var sum = 0;
        for (var i = 0; i < 13; i++)
        {
            var d = body13[i] - '0';
            sum += ((12 - i) % 2 == 0) ? d * 3 : d;
        }
        return body13 + (10 - (sum % 10)) % 10;
    }
}
