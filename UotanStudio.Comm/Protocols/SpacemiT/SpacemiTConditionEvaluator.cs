namespace UotanStudio.Comm.Protocols.SpacemiT;

/// <summary>
/// Evaluates the tiny <c>skip_when</c> guard expressions used by SpacemiT
/// <c>fastboot.yaml</c> scripts. The only constructs the vendor scripts use are a
/// variable reference and an optional leading <c>not</c>, so anything else fails the
/// parse instead of being silently treated as true or false.
/// <para>求值 SpacemiT <c>fastboot.yaml</c> 使用的极简 <c>skip_when</c> 守卫表达式。
/// 厂商脚本只用到变量引用与可选的前导 <c>not</c>，其余任何构造都会使解析失败，
/// 而不是被静默当作真或假。</para>
/// </summary>
public static class SpacemiTConditionEvaluator
{
    /// <summary>
    /// Evaluates a guard expression against a variable bag.
    /// <para>对变量包求值守卫表达式。</para>
    /// </summary>
    /// <param name="expression">Expression text such as <c>temp.version</c> or <c>not temp.version</c>; <see langword="null"/> or empty means "always run". <para>表达式文本（如 <c>temp.version</c> 或 <c>not temp.version</c>）；为 <see langword="null"/> 或空时表示“总是执行”。</para></param>
    /// <param name="variables">Case-sensitive variable bag filled by earlier <c>getvar</c> actions. <para>由先前 <c>getvar</c> 动作填充的大小写敏感变量包。</para></param>
    /// <returns><see langword="true"/> when the action guarded by this expression must be skipped. <para>该表达式守卫的动作需要跳过时为 <see langword="true"/>。</para></returns>
    /// <exception cref="InvalidDataException">The expression uses an unsupported construct or a variable name is malformed. <para>表达式使用了不受支持的构造，或变量名格式非法。</para></exception>
    public static bool ShouldSkip(string? expression, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        var tokens = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length > 2)
        {
            throw new InvalidDataException($"skip_when expression '{expression}' uses unsupported syntax.");
        }

        var negate = tokens.Length == 2;
        if (negate)
        {
            if (!tokens[0].Equals("not", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"skip_when expression '{expression}' uses unsupported operator '{tokens[0]}'.");
            }
        }

        var operand = tokens[^1];
        if (!IsSupportedOperand(operand))
        {
            throw new InvalidDataException($"skip_when expression '{expression}' references unsupported operand '{operand}'.");
        }

        var value = variables.TryGetValue(operand, out var found) ? found : string.Empty;
        var truthy = IsTruthy(value);
        return negate ? !truthy : truthy;
    }

    private static bool IsSupportedOperand(string operand)
    {
        if (operand.Length == 0 || operand.Length > 64)
        {
            return false;
        }

        foreach (var ch in operand)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTruthy(string value) =>
        value.Length > 0 &&
        !value.Equals("null", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("0", StringComparison.Ordinal) &&
        !value.Equals("false", StringComparison.OrdinalIgnoreCase);
}