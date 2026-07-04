namespace Terminal.Buffer;

internal readonly record struct CsiCommand(
    char Final,
    char Prefix,
    string Intermediate,
    string ParameterText,
    int?[] Parameters,
    string RawParameters)
{
    public bool IsPrivate => Prefix == '?';
    public bool IsSecondary => Prefix == '>';
}

internal static class CsiDecoder
{
    private static readonly char[] IntermediateCharacters =
        [' ', '!', '"', '#', '$', '%', '&', '\'', '(', ')', '*', '+', ',', '-', '.', '/'];

    public static CsiCommand Decode(char final, string rawParameters)
    {
        char prefix = rawParameters.Length > 0 && rawParameters[0] is '?' or '>' or '<' or '='
            ? rawParameters[0]
            : '\0';
        bool stripsPrefixFromParameters = prefix is '?' or '>';
        string parameterSection = stripsPrefixFromParameters ? rawParameters[1..] : rawParameters;
        int intermediateIndex = parameterSection.IndexOfAny(IntermediateCharacters);
        string intermediate = intermediateIndex >= 0 ? parameterSection[intermediateIndex..] : string.Empty;
        string parameterText = intermediateIndex >= 0
            ? parameterSection[..intermediateIndex]
            : parameterSection;

        return new CsiCommand(
            final,
            prefix,
            intermediate,
            parameterText,
            ParseParameterList(parameterText),
            rawParameters);
    }

    public static int?[] ParseParameterList(string parameterText)
    {
        if (string.IsNullOrEmpty(parameterText))
        {
            return [];
        }

        string[] parts = parameterText.Split(';');
        var result = new int?[parts.Length];
        for (int index = 0; index < parts.Length; index++)
        {
            if (int.TryParse(parts[index], out int value))
            {
                result[index] = value;
            }
        }

        return result;
    }
}
