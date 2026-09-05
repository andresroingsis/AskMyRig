using System.Globalization;
using System.Text;

namespace AskMyRig.Core;

/// <summary>
/// Formats a float array the way SQL Server's VECTOR type expects.
///
/// Shared between ingestion (writing chunk vectors) and search (passing the
/// query vector), because getting this wrong in only one of the two places
/// would be a genuinely confusing bug to chase.
/// </summary>
public static class VectorLiteral
{
    public static string From(float[] vector)
    {
        var builder = new StringBuilder(vector.Length * 12);
        builder.Append('[');

        for (var i = 0; i < vector.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            // InvariantCulture is essential. On a machine with a comma decimal
            // separator the default would emit "[0,13,-0,04]" and SQL Server
            // would reject it with an unhelpful cast error.
            builder.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }

        builder.Append(']');
        return builder.ToString();
    }
}
