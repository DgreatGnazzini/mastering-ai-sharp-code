namespace PriorAuthBot.Core.Intake;

/// <summary>
/// The National Provider Identifier check digit (chapter 4). An NPI is ten digits, and the last one
/// is a Luhn check digit computed with the prefix 80840. A model that misreads one digit almost
/// always produces an NPI that fails this check, which is why it is worth ten lines of code.
/// </summary>
public static class Npi
{
    public static bool IsValid(string? npi)
    {
        if (npi is not { Length: 10 } || !npi.All(char.IsAsciiDigit))
        {
            return false;
        }

        // Luhn over "80840" + the first nine digits, then compare with the tenth.
        var digits = "80840" + npi[..9];
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 0)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        return (10 - (sum % 10)) % 10 == npi[9] - '0';
    }
}
