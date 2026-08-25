package dev.kyletaylor.infraadvisor.mobile;

import android.content.Intent;
import android.os.Bundle;
import android.view.Menu;
import android.view.MenuItem;
import android.view.View;
import android.widget.Button;
import android.widget.TextView;
import androidx.appcompat.app.AlertDialog;
import androidx.appcompat.app.AppCompatActivity;
import androidx.appcompat.widget.Toolbar;
import com.datadog.android.rum.GlobalRumMonitor;
import com.datadog.android.rum.RumErrorSource;
import dev.kyletaylor.infraadvisor.mobile.api.ApiError;
import dev.kyletaylor.infraadvisor.mobile.model.LoginResponse;
import java.util.Map;
import org.json.JSONObject;

/** Interactive examples for RUM errors, instrumented HTTP failures, logs, and native crashes. */
public final class ErrorLabActivity extends AppCompatActivity {
    private LoginResponse login;
    private TextView status;
    private Button apiError;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        login = app().session().getLogin();
        if (login == null) { openLogin(); return; }
        setContentView(R.layout.activity_error_lab);
        SystemBarInsets.apply(findViewById(R.id.screen_root));
        setSupportActionBar((Toolbar) findViewById(R.id.toolbar));
        setTitle(R.string.error_lab);
        AppTabs.bind(this, AppTabs.Destination.ERRORS);
        status = findViewById(R.id.error_lab_status);
        apiError = findViewById(R.id.api_error);
        findViewById(R.id.handled_error).setOnClickListener(view -> recordHandledError());
        apiError.setOnClickListener(view -> requestApiError());
        findViewById(R.id.sample_logs).setOnClickListener(view -> sendSampleLogs());
        configureCrashButton();
    }

    private void recordHandledError() {
        IllegalStateException error = new IllegalStateException("Intentional handled Error Lab exception");
        Map<String, Object> attributes = InfraAdvisorApplication.demoAttributes("handled_mobile_error");
        attributes.put("demo.error.kind", "handled");
        GlobalRumMonitor.get().addError("Intentional handled mobile error", RumErrorSource.CUSTOM, error, attributes);
        app().logger().e("Intentional handled mobile error", error, attributes);
        status.setText("Handled RUM error and correlated error log recorded.");
    }

    private void requestApiError() {
        apiError.setEnabled(false);
        status.setText("Requesting an intentionally missing route…");
        app().api().simulateApiError(login.token, new ApiClient.Result<JSONObject>() {
            @Override public void success(JSONObject ignored) {
                apiError.setEnabled(true);
                status.setText("The route unexpectedly succeeded.");
            }
            @Override public void failure(Exception failure) {
                apiError.setEnabled(true);
                Map<String, Object> attributes = InfraAdvisorApplication.demoAttributes("expected_api_failure");
                if (failure instanceof com.android.volley.VolleyError) {
                    com.android.volley.VolleyError volleyError = (com.android.volley.VolleyError) failure;
                    if (volleyError.networkResponse != null) attributes.put("http.status_code", volleyError.networkResponse.statusCode);
                    status.setText("Expected API failure captured: " + ApiError.message(volleyError));
                } else {
                    status.setText("Expected transport failure captured.");
                }
                app().logger().w("Intentional API response error observed", failure, attributes);
            }
        });
    }

    private void sendSampleLogs() {
        app().logger().i("Intentional demo information log", null, InfraAdvisorApplication.demoAttributes("sample_info"));
        app().logger().w("Intentional demo warning log", null, InfraAdvisorApplication.demoAttributes("sample_warning"));
        app().logger().e("Intentional demo error log", null, InfraAdvisorApplication.demoAttributes("sample_error"));
        status.setText("Three correlated sample logs queued for upload.");
    }

    private void configureCrashButton() {
        Button crash = findViewById(R.id.trigger_crash);
        if (!BuildConfig.DEBUG) return;
        crash.setVisibility(View.VISIBLE);
        crash.setOnClickListener(view -> new AlertDialog.Builder(this)
                .setTitle("Crash the demo app?")
                .setMessage("Unsaved in-memory session state will be lost. Reopen the app afterward so Datadog can upload the crash.")
                .setNegativeButton("Cancel", null)
                .setPositiveButton("Crash now", (dialog, which) -> {
                    throw new IllegalStateException("Intentional Infra Advisor Android demo crash");
                })
                .show());
    }

    @Override public boolean onCreateOptionsMenu(Menu menu) {
        menu.add(R.string.logout).setShowAsAction(MenuItem.SHOW_AS_ACTION_NEVER);
        return true;
    }

    @Override public boolean onOptionsItemSelected(MenuItem item) {
        if (item.getTitle().equals(getString(R.string.logout))) { app().session().clear(); openLogin(); return true; }
        return super.onOptionsItemSelected(item);
    }

    private void openLogin() {
        Intent intent = new Intent(this, LoginActivity.class);
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK);
        startActivity(intent);
        finish();
    }

    private InfraAdvisorApplication app() { return (InfraAdvisorApplication) getApplication(); }
}
