using Microsoft.Extensions.Logging;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.OperationalInsights;
using Azure.Core;
using Azure;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ChatAgent.Application.Tools.Azure;

public class FindConnectorSolutionHandler(ILogger<FindConnectorSolutionHandler> logger)
{
    private readonly ILogger<FindConnectorSolutionHandler> _logger = logger;
    private readonly HttpClient _httpClient = new();

    public async Task<FindConnectorSolution.Output> HandleAsync(
        FindConnectorSolution.Input input,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Finding connector solution '{ConnectorName}' for workspace '{WorkspaceName}' in subscription '{SubscriptionId}' and resource group '{ResourceGroupName}'",
            input.ConnectorName,
            input.WorkspaceName,
            input.SubscriptionId,
            input.ResourceGroupName);

        try
        {
            // 1. Create Azure Resource Manager client with DefaultAzureCredential
            var credential = new DefaultAzureCredential();
            var armClient = new ArmClient(credential);

            // 2. Get the subscription
            var subscriptionId = $"/subscriptions/{input.SubscriptionId}";
            var subscription = await armClient.GetSubscriptionResource(new ResourceIdentifier(subscriptionId))
                .GetAsync(cancellationToken);

            if (subscription == null || !subscription.HasValue)
            {
                _logger.LogWarning("Subscription {SubscriptionId} not found", input.SubscriptionId);
                return new FindConnectorSolution.Output(
                    SolutionId: string.Empty,
                    SolutionName: string.Empty,
                    Description: "Subscription not found",
                    Version: string.Empty,
                    IsInstalled: false
                );
            }

            // 3. Get the resource group
            var resourceGroup = await subscription.Value.GetResourceGroupAsync(input.ResourceGroupName, cancellationToken);

            if (resourceGroup == null || !resourceGroup.HasValue)
            {
                _logger.LogWarning("Resource group {ResourceGroupName} not found", input.ResourceGroupName);
                return new FindConnectorSolution.Output(
                    SolutionId: string.Empty,
                    SolutionName: string.Empty,
                    Description: "Resource group not found",
                    Version: string.Empty,
                    IsInstalled: false
                );
            }

            // 4. Get the Log Analytics workspace
            var workspaceCollection = resourceGroup.Value.GetOperationalInsightsWorkspaces();
            var workspace = await workspaceCollection.GetAsync(input.WorkspaceName, cancellationToken);

            if (workspace == null || !workspace.HasValue)
            {
                _logger.LogWarning("Workspace {WorkspaceName} not found", input.WorkspaceName);
                return new FindConnectorSolution.Output(
                    SolutionId: string.Empty,
                    SolutionName: string.Empty,
                    Description: "Workspace not found",
                    Version: string.Empty,
                    IsInstalled: false
                );
            }

            // 5. Search for connector solutions dynamically
            var workspaceResource = workspace.Value;
            var foundSolutions = new List<ConnectorSolutionInfo>();
            var installedSolutions = new List<ConnectorSolutionInfo>();

            try
            {
                // Get access token for Azure Management API
                var scopes = new[] { "https://management.azure.com/.default" };
                var accessToken = await credential.GetTokenAsync(
                    new TokenRequestContext(scopes),
                    cancellationToken);

                // Query ALL installed solutions via Content Hub API
                var solutionsUrl = $"https://management.azure.com/subscriptions/{input.SubscriptionId}" +
                    $"/resourceGroups/{input.ResourceGroupName}" +
                    $"/providers/Microsoft.OperationalInsights/workspaces/{input.WorkspaceName}" +
                    $"/providers/Microsoft.SecurityInsights/contentProductPackages?api-version=2024-09-01";

                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

                var solutionsResponse = await _httpClient.GetAsync(solutionsUrl, cancellationToken);
                if (solutionsResponse.IsSuccessStatusCode)
                {
                    var solutionsJson = await solutionsResponse.Content.ReadAsStringAsync(cancellationToken);
                    var solutionsData = JsonDocument.Parse(solutionsJson);

                    if (solutionsData.RootElement.TryGetProperty("value", out var solutions))
                    {
                        foreach (var solution in solutions.EnumerateArray())
                        {
                            var solutionInfo = ParseSolutionInfo(solution);
                            if (solutionInfo != null)
                            {
                                // Check if this solution matches the search query
                                if (MatchesConnectorSearch(solutionInfo, input.ConnectorName))
                                {
                                    foundSolutions.Add(solutionInfo);
                                    if (solutionInfo.IsInstalled)
                                    {
                                        installedSolutions.Add(solutionInfo);
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("Failed to query content packages: {StatusCode} - {Reason}",
                        solutionsResponse.StatusCode, solutionsResponse.ReasonPhrase);
                }

                // Also query data connectors to find installed connectors
                var dataConnectorsUrl = $"https://management.azure.com/subscriptions/{input.SubscriptionId}" +
                    $"/resourceGroups/{input.ResourceGroupName}" +
                    $"/providers/Microsoft.OperationalInsights/workspaces/{input.WorkspaceName}" +
                    $"/providers/Microsoft.SecurityInsights/dataConnectors?api-version=2024-09-01";

                var connectorsResponse = await _httpClient.GetAsync(dataConnectorsUrl, cancellationToken);
                if (connectorsResponse.IsSuccessStatusCode)
                {
                    var connectorsJson = await connectorsResponse.Content.ReadAsStringAsync(cancellationToken);
                    var connectorsData = JsonDocument.Parse(connectorsJson);

                    if (connectorsData.RootElement.TryGetProperty("value", out var connectors))
                    {
                        foreach (var connector in connectors.EnumerateArray())
                        {
                            if (connector.TryGetProperty("kind", out var kind))
                            {
                                var kindStr = kind.GetString();
                                _logger.LogInformation("Found installed connector: {Kind}", kindStr);

                                // Try to match this with our found solutions
                                foreach (var solution in foundSolutions)
                                {
                                    if (solution.DataConnectorKinds.Contains(kindStr))
                                    {
                                        solution.IsInstalled = true;
                                    }
                                }
                            }
                        }
                    }
                }

                // Query available solutions from Content Hub templates (not yet installed)
                var templatesUrl = $"https://management.azure.com/subscriptions/{input.SubscriptionId}" +
                    $"/resourceGroups/{input.ResourceGroupName}" +
                    $"/providers/Microsoft.OperationalInsights/workspaces/{input.WorkspaceName}" +
                    $"/providers/Microsoft.SecurityInsights/contentTemplates?api-version=2024-09-01";

                var templatesResponse = await _httpClient.GetAsync(templatesUrl, cancellationToken);
                if (templatesResponse.IsSuccessStatusCode)
                {
                    var templatesJson = await templatesResponse.Content.ReadAsStringAsync(cancellationToken);
                    var templatesData = JsonDocument.Parse(templatesJson);

                    if (templatesData.RootElement.TryGetProperty("value", out var templates))
                    {
                        foreach (var template in templates.EnumerateArray())
                        {
                            var templateInfo = ParseTemplateInfo(template);
                            if (templateInfo != null && MatchesConnectorSearch(templateInfo, input.ConnectorName))
                            {
                                // Check if not already in found solutions
                                if (!foundSolutions.Any(s => s.SolutionId == templateInfo.SolutionId))
                                {
                                    foundSolutions.Add(templateInfo);
                                }
                            }
                        }
                    }
                }
            }
            catch (RequestFailedException ex)
            {
                _logger.LogWarning("Could not check installed connectors via API: {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error checking connector status: {Message}", ex.Message);
            }

            // 6. Return the best matching solution or multiple options
            if (foundSolutions.Count == 0)
            {
                _logger.LogInformation("No solutions found for connector '{ConnectorName}'", input.ConnectorName);
                return new FindConnectorSolution.Output(
                    SolutionId: string.Empty,
                    SolutionName: input.ConnectorName,
                    Description: $"No solutions found for '{input.ConnectorName}'. Please check the connector name or browse available solutions in Microsoft Sentinel Content Hub.",
                    Version: string.Empty,
                    IsInstalled: false
                );
            }
            else if (foundSolutions.Count == 1)
            {
                var solution = foundSolutions.First();
                _logger.LogInformation("Found solution: {SolutionName} (Installed: {IsInstalled})",
                    solution.SolutionName, solution.IsInstalled);

                return new FindConnectorSolution.Output(
                    SolutionId: solution.SolutionId,
                    SolutionName: solution.SolutionName,
                    Description: solution.Description,
                    Version: solution.Version,
                    IsInstalled: solution.IsInstalled
                );
            }
            else
            {
                // Multiple solutions found - return the most relevant one or let the agent present options
                var bestMatch = SelectBestMatch(foundSolutions, input.ConnectorName);

                var optionsText = $"Found {foundSolutions.Count} matching solutions:\n";
                foreach (var sol in foundSolutions.Take(5))
                {
                    optionsText += $"- {sol.SolutionName} v{sol.Version} (Installed: {sol.IsInstalled})\n";
                }

                _logger.LogInformation("Multiple solutions found for '{ConnectorName}': {Count} matches",
                    input.ConnectorName, foundSolutions.Count);

                return new FindConnectorSolution.Output(
                    SolutionId: bestMatch.SolutionId,
                    SolutionName: bestMatch.SolutionName,
                    Description: optionsText + $"\nRecommended: {bestMatch.SolutionName} - {bestMatch.Description}",
                    Version: bestMatch.Version,
                    IsInstalled: bestMatch.IsInstalled
                );
            }
        }
        catch (AuthenticationFailedException ex)
        {
            _logger.LogError(ex, "Authentication failed when accessing Azure resources");
            return new FindConnectorSolution.Output(
                SolutionId: string.Empty,
                SolutionName: string.Empty,
                Description: $"Authentication failed: {ex.Message}",
                Version: string.Empty,
                IsInstalled: false
            );
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Azure request failed");
            return new FindConnectorSolution.Output(
                SolutionId: string.Empty,
                SolutionName: string.Empty,
                Description: $"Azure request failed: {ex.Message}",
                Version: string.Empty,
                IsInstalled: false
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error finding connector solution");
            return new FindConnectorSolution.Output(
                SolutionId: string.Empty,
                SolutionName: string.Empty,
                Description: $"Error: {ex.Message}",
                Version: string.Empty,
                IsInstalled: false
            );
        }
    }

    private ConnectorSolutionInfo? ParseSolutionInfo(JsonElement solution)
    {
        try
        {
            var info = new ConnectorSolutionInfo();

            if (solution.TryGetProperty("name", out var name))
                info.SolutionId = name.GetString() ?? string.Empty;

            if (solution.TryGetProperty("properties", out var properties))
            {
                if (properties.TryGetProperty("displayName", out var displayName))
                    info.SolutionName = displayName.GetString() ?? string.Empty;

                if (properties.TryGetProperty("description", out var description))
                    info.Description = description.GetString() ?? string.Empty;

                if (properties.TryGetProperty("version", out var version))
                    info.Version = version.GetString() ?? "1.0.0";

                if (properties.TryGetProperty("installedVersion", out var installed))
                {
                    info.Version = installed.GetString() ?? info.Version;
                    info.IsInstalled = true;
                }

                if (properties.TryGetProperty("isInstalled", out var isInstalled))
                    info.IsInstalled = isInstalled.GetBoolean();

                if (properties.TryGetProperty("contentKind", out var contentKind))
                    info.ContentKind = contentKind.GetString() ?? string.Empty;

                if (properties.TryGetProperty("contentProductId", out var productId))
                    info.ProductId = productId.GetString() ?? string.Empty;

                // Extract data connector kinds if available
                if (properties.TryGetProperty("dependencies", out var dependencies) &&
                    dependencies.TryGetProperty("data", out var data))
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("kind", out var kind))
                        {
                            var kindStr = kind.GetString();
                            if (!string.IsNullOrEmpty(kindStr))
                                info.DataConnectorKinds.Add(kindStr);
                        }
                    }
                }
            }

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to parse solution info: {Message}", ex.Message);
            return null;
        }
    }

    private ConnectorSolutionInfo? ParseTemplateInfo(JsonElement template)
    {
        try
        {
            var info = new ConnectorSolutionInfo();

            if (template.TryGetProperty("name", out var name))
                info.SolutionId = name.GetString() ?? string.Empty;

            if (template.TryGetProperty("properties", out var properties))
            {
                if (properties.TryGetProperty("displayName", out var displayName))
                    info.SolutionName = displayName.GetString() ?? string.Empty;

                if (properties.TryGetProperty("contentKind", out var contentKind))
                {
                    info.ContentKind = contentKind.GetString() ?? string.Empty;
                    // Only process DataConnector templates
                    if (info.ContentKind != "DataConnector" && info.ContentKind != "Solution")
                        return null;
                }

                if (properties.TryGetProperty("source", out var source) &&
                    source.TryGetProperty("name", out var sourceName))
                {
                    info.SolutionName = sourceName.GetString() ?? info.SolutionName;
                }

                if (properties.TryGetProperty("packageVersion", out var version))
                    info.Version = version.GetString() ?? "1.0.0";

                info.IsInstalled = false; // Templates are not installed by definition
            }

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to parse template info: {Message}", ex.Message);
            return null;
        }
    }

    private bool MatchesConnectorSearch(ConnectorSolutionInfo solution, string searchQuery)
    {
        var query = searchQuery.ToLowerInvariant();

        // Check solution name
        if (solution.SolutionName.ToLowerInvariant().Contains(query))
            return true;

        // Check description
        if (!string.IsNullOrEmpty(solution.Description) &&
            solution.Description.ToLowerInvariant().Contains(query))
            return true;

        // Check product ID
        if (!string.IsNullOrEmpty(solution.ProductId) &&
            solution.ProductId.ToLowerInvariant().Contains(query))
            return true;

        // Check data connector kinds
        if (solution.DataConnectorKinds.Any(k => k.ToLowerInvariant().Contains(query)))
            return true;

        // Special cases for common aliases
        var aliases = GetConnectorAliases(query);
        foreach (var alias in aliases)
        {
            if (solution.SolutionName.ToLowerInvariant().Contains(alias) ||
                (!string.IsNullOrEmpty(solution.Description) &&
                 solution.Description.ToLowerInvariant().Contains(alias)))
                return true;
        }

        return false;
    }

    private List<string> GetConnectorAliases(string connectorName)
    {
        var aliases = new List<string>();
        var lower = connectorName.ToLowerInvariant();

        // Common connector aliases
        var aliasMap = new Dictionary<string, List<string>>
        {
            ["aws"] = new() { "amazon", "s3", "cloudtrail", "amazon web services" },
            ["azure"] = new() { "azure activity", "azure ad", "azure active directory", "microsoft" },
            ["gcp"] = new() { "google", "google cloud", "google cloud platform" },
            ["office"] = new() { "office 365", "o365", "microsoft 365", "m365" },
            ["defender"] = new() { "microsoft defender", "mde", "mdatp", "defender for endpoint" },
            ["sentinel"] = new() { "azure sentinel", "microsoft sentinel" },
            ["firewall"] = new() { "fw", "network security", "nsg" },
            ["vm"] = new() { "virtual machine", "virtual machines" },
            ["ad"] = new() { "active directory", "azure ad", "azure active directory" }
        };

        foreach (var kvp in aliasMap)
        {
            if (lower.Contains(kvp.Key) || kvp.Value.Any(v => lower.Contains(v)))
            {
                aliases.AddRange(kvp.Value);
                aliases.Add(kvp.Key);
            }
        }

        return aliases.Distinct().ToList();
    }

    private ConnectorSolutionInfo SelectBestMatch(List<ConnectorSolutionInfo> solutions, string searchQuery)
    {
        // Prioritize:
        // 1. Already installed solutions
        // 2. Exact name matches
        // 3. Solutions with "Solution" content kind
        // 4. Most recent version

        var query = searchQuery.ToLowerInvariant();

        var scored = solutions.Select(s => new
        {
            Solution = s,
            Score = CalculateMatchScore(s, query)
        })
        .OrderByDescending(x => x.Score)
        .ThenByDescending(x => x.Solution.IsInstalled)
        .ThenByDescending(x => x.Solution.Version)
        .ToList();

        return scored.First().Solution;
    }

    private int CalculateMatchScore(ConnectorSolutionInfo solution, string query)
    {
        int score = 0;

        // Exact name match
        if (solution.SolutionName.ToLowerInvariant() == query)
            score += 100;

        // Name starts with query
        if (solution.SolutionName.ToLowerInvariant().StartsWith(query))
            score += 50;

        // Name contains query
        if (solution.SolutionName.ToLowerInvariant().Contains(query))
            score += 25;

        // Is a complete solution (not just a connector)
        if (solution.ContentKind == "Solution")
            score += 20;

        // Already installed
        if (solution.IsInstalled)
            score += 15;

        // Has description
        if (!string.IsNullOrEmpty(solution.Description))
            score += 5;

        return score;
    }

    private class ConnectorSolutionInfo
    {
        public string SolutionId { get; set; } = string.Empty;
        public string SolutionName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public bool IsInstalled { get; set; }
        public string ContentKind { get; set; } = string.Empty;
        public string ProductId { get; set; } = string.Empty;
        public List<string> DataConnectorKinds { get; set; } = new();
    }
}