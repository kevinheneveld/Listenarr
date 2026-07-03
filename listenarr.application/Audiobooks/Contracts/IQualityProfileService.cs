
namespace Listenarr.Application.Audiobooks.Contracts
{
    public interface IQualityProfileService
    {
        Task<List<QualityProfile>> GetAllAsync();
        Task<QualityProfile?> GetByIdAsync(int id);
        Task<QualityProfile?> GetDefaultAsync();
        Task<QualityProfile> CreateAsync(QualityProfile profile);
        Task<QualityProfile> UpdateAsync(QualityProfile profile);
        Task<bool> DeleteAsync(int id);
        // expectedRuntimeMinutes (the book's catalog runtime) enables the
        // runtime-relative size sanity check; null skips it (e.g. profile
        // testing without a book context).
        Task<QualityScore> ScoreSearchResult(SearchResult searchResult, QualityProfile profile, int? expectedRuntimeMinutes = null);
        Task<List<QualityScore>> ScoreSearchResults(List<SearchResult> searchResults, QualityProfile profile, int? expectedRuntimeMinutes = null);
    }
}
