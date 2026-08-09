using System.Security.Cryptography;

namespace AlgoJudge.Server.Utils
{
    /// <summary>
    /// Passwords this Server generates, in one place.
    /// <para>
    /// Two callers with two different needs — a manager handing a temporary
    /// account out on paper, and the seed setting one nobody will ever read —
    /// and one generator, because two of them is how the alphabet or the source
    /// drifts in one and not the other.
    /// </para>
    /// </summary>
    public static class Passwords
    {
        /// <summary>
        /// No <c>l</c>, no <c>1</c>, no <c>0</c>, no <c>O</c>.
        /// <para>
        /// These are read off a screen and typed by somebody else, so the
        /// characters that are read as each other are simply not in the
        /// alphabet. It costs about a fifth of a bit per character and saves the
        /// support conversation.
        /// </para>
        /// </summary>
        private const string Alphabet = "abcdefghijkmnopqrstuvwxyz23456789";

        /// <summary>
        /// <paramref name="length"/> characters from a cryptographic source.
        /// <para>
        /// <c>RandomNumberGenerator</c> rather than <c>Random</c>: the second is
        /// seeded from the clock, and two accounts created in the same
        /// millisecond can be given the same password.
        /// </para>
        /// </summary>
        public static string Generate(int length)
        {
            var chars = new char[length];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
            return new string(chars);
        }
    }
}
