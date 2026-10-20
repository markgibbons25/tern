# Detect CM features instead of claiming an Official CLI version

Management Services rejects any client whose major version is lower than its own. The only ways past that check are to send the Official CLI's version number, or to tell the server not to compare versions. We tell it not to compare. Instead, we inspect each CM's GraphQL schema once, cache the result per Endpoint, and switch features on or off based on what the schema actually offers.

Claiming the Official CLI's version would be dishonest. It would also break the first time Sitecore bumps a major version, and it says nothing about what a given CM supports across Management Services 5.1, 5.2 and 6.0.
