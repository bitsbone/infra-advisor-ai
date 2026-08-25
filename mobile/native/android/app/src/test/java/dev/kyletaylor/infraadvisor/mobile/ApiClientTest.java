package dev.kyletaylor.infraadvisor.mobile;

import static org.junit.Assert.assertEquals;

import org.junit.Test;

public final class ApiClientTest {
    @Test public void backendSelectsMatchingApiPrefix() {
        assertEquals("/api/query", ApiClient.apiPath(ApiClient.BACKEND_PYTHON, "/query"));
        assertEquals("/api-dotnet/query", ApiClient.apiPath(ApiClient.BACKEND_DOTNET, "/query"));
        assertEquals("/api-dotnet/models", ApiClient.apiPath(ApiClient.BACKEND_DOTNET, "/models"));
    }
}
