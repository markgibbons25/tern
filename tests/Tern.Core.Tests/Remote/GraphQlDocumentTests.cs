using Tern.Remote.Authoring;

namespace Tern.Core.Tests.Remote;

public class GraphQlDocumentTests
{
    [Theory]
    [InlineData("mutation { updateItem(input: {}) { item { path } } }")]
    [InlineData("mutation Rename($id: ID!) { renameItem(input: { itemId: $id }) { item { name } } }")]
    [InlineData("query A { item { path } }\nmutation B { deleteItem(input: {}) { successful } }")]
    [InlineData("# a comment\nmutation{deleteItem(input:{}){successful}}")]
    [InlineData("fragment F on Item { path }\nmutation { createItem(input: {}) { item { ...F } } }")]
    public void Finds_a_mutation(string document) => Assert.True(GraphQlDocument.HasMutation(document));

    [Theory]
    [InlineData("{ item(where: { path: \"/sitecore/content\" }) { path } }")]
    [InlineData("query { item { path } }")]
    [InlineData("query Q($m: String = \"mutation\") { item(path: $m) { path } }")]
    [InlineData("# mutation in a comment\nquery { item { path } }")]
    [InlineData("query { mutation: item { path } }")]
    [InlineData("query { item { field(name: \"\"\"a \\\"\"\" mutation\"\"\") { value } } }")]
    [InlineData("query { item { Mutation } }")]
    public void Lets_a_query_through(string document) => Assert.False(GraphQlDocument.HasMutation(document));
}
