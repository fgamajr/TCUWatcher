using System;
using System.Text.RegularExpressions;

namespace TCUWatcher.API.Utils
{
    public static class TCUProcessValidator
    {
        /// <summary>
        /// Calculates the check digit (DV) for a TCU process number.
        /// </summary>
        /// <param name="numeroProcessoSemPonto">The 6-digit process number string without dots (e.g., "006226").</param>
        /// <param name="anoComQuatroDigitos">The 4-digit year string (e.g., "2017").</param>
        /// <returns>The calculated check digit.</returns>
        /// <exception cref="ArgumentException">If input parameters are invalid.</exception>
        public static int CalculateDV(string numeroProcessoSemPonto, string anoComQuatroDigitos)
        {
            string numeroProcessoLimpo = numeroProcessoSemPonto.Replace(".", ""); // Remove dots if any
            string anoDoisDigitos;

            if (anoComQuatroDigitos.Length == 4)
            {
                anoDoisDigitos = anoComQuatroDigitos.Substring(2, 2);
            }
            else if (anoComQuatroDigitos.Length == 2)
            {
                anoDoisDigitos = anoComQuatroDigitos;
            }
            else
            {
                throw new ArgumentException("Ano inválido. Deve ter 2 ou 4 dígitos.", nameof(anoComQuatroDigitos));
            }
            
            if (numeroProcessoLimpo.Length != 6) 
            {
                throw new ArgumentException("Número do processo (sem pontos) deve ter 6 dígitos.", nameof(numeroProcessoSemPonto));
            }

            string nCompletoStr = numeroProcessoLimpo + anoDoisDigitos; 
            int soma = 0;
            int multiplicador = 2;

            for (int i = nCompletoStr.Length - 1; i >= 0; i--)
            {
                if (!int.TryParse(nCompletoStr[i].ToString(), out int digito))
                    throw new ArgumentException("Número do processo contém caracteres não numéricos após limpeza.");
                
                soma += digito * multiplicador;
                multiplicador++;
                if (multiplicador > 9)
                {
                    multiplicador = 2;
                }
            }
            int resto = soma % 11;
            int dv = 11 - resto;
            return (dv >= 10) ? 0 : dv;
        }

        /// <summary>
        /// Verifies the check digit of a full TCU process number string.
        /// </summary>
        /// <param name="fullProcessNumber">The full process number, e.g., "006.226/2017-5".</param>
        /// <returns>True if the check digit is valid, false otherwise.</returns>
        public static bool VerifyDV(string fullProcessNumber)
        {
            if (string.IsNullOrWhiteSpace(fullProcessNumber)) return false;

            try
            {
                var match = Regex.Match(fullProcessNumber.Trim(), @"^(\d{3}\.\d{3}|\d{6})/(\d{4})-(\d)$");
                if (!match.Success || match.Groups.Count != 4) return false;
                
                string numeroProcessoComOuSemPonto = match.Groups[1].Value; 
                string anoComQuatroDigitos = match.Groups[2].Value;   
                if (!int.TryParse(match.Groups[3].Value, out int dvInformado)) return false;
                
                string numeroProcessoSemPonto = numeroProcessoComOuSemPonto.Replace(".", "");
                
                int dvCalculado = CalculateDV(numeroProcessoSemPonto, anoComQuatroDigitos);
                return dvInformado == dvCalculado;
            }
            catch (ArgumentException) 
            {
                return false;
            }
            catch (Exception) 
            {
                return false;
            }
        }
    }
}