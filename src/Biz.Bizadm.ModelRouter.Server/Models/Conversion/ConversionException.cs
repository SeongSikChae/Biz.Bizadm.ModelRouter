namespace Biz.Bizadm.ModelRouter.Server.Models.Conversion
{
    /// <summary>
    /// 프로토콜 변환이 불가능한 요청(구조 오류, 대응 없는 서버 도구·블록). 컨트롤러가 입력 프로토콜 형식의 400으로 응답한다.
    /// </summary>
    public sealed class ConversionException : Exception
    {
        public ConversionException(string message)
            : base(message)
        {
        }

        public ConversionException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>JSON 값 타입 불일치 등 변환 중 예외를 ConversionException으로 감싼다.</summary>
        internal static T Guard<T>(Func<T> convert)
        {
            try
            {
                return convert();
            }
            catch (ConversionException)
            {
                throw;
            }
            catch (Exception e) when (e is InvalidOperationException
                or FormatException
                or NullReferenceException
                or KeyNotFoundException
                or ArgumentException
                or IndexOutOfRangeException)
            {
                throw new ConversionException($"Request body has an unexpected structure: {e.Message}", e);
            }
        }
    }
}
