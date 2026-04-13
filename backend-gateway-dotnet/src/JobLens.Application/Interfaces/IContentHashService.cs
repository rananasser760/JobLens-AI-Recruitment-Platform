namespace JobLens.Application.Interfaces;

public interface IContentHashService
{
    string Compute(byte[] bytes);
    string Compute(string content);
}
