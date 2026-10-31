using System.Text.Json;
using PlantOps.Application.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class InvestigationSearchQueryTests
{
    [Theory]
    [InlineData("temperature 温升", true)]
    [InlineData("temperature TEMPERATURE temperature", true)]
    [InlineData("one two three four five six seven eight nine ten eleven twelve", true)]
    [InlineData("one two three four five six seven eight nine ten eleven twelve thirteen", false)]
    [InlineData("!!!", false)]
    public void ToolArgumentsMatchKnowledgeSearchTermContract(string query, bool accepted)
    {
        var arguments = JsonSerializer.SerializeToElement(new
        {
            query,
            limit = 6
        });
        if (accepted)
        {
            InvestigationToolPolicy.Validate("search_documents", arguments);
        }
        else
        {
            Assert.Throws<LlmException>(() => InvestigationToolPolicy.Validate("search_documents", arguments));
        }
    }
}
