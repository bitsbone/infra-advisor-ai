package dev.kyletaylor.infraadvisor.mobile;

import android.content.Intent;
import android.os.Bundle;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.ProgressBar;
import android.widget.TextView;
import androidx.appcompat.app.AppCompatActivity;
import androidx.appcompat.widget.Toolbar;
import dev.kyletaylor.infraadvisor.mobile.api.ApiError;
import dev.kyletaylor.infraadvisor.mobile.model.LoginResponse;

public final class LoginActivity extends AppCompatActivity {
    private Button signIn;
    private ProgressBar progress;
    private TextView error;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        setContentView(R.layout.activity_login);
        SystemBarInsets.apply(findViewById(R.id.screen_root));
        setSupportActionBar((Toolbar) findViewById(R.id.toolbar));
        setTitle(R.string.login_title);
        EditText email = findViewById(R.id.email);
        EditText password = findViewById(R.id.password);
        signIn = findViewById(R.id.sign_in);
        progress = findViewById(R.id.progress);
        error = findViewById(R.id.error);
        signIn.setOnClickListener(view -> {
            if (email.getText().toString().trim().isEmpty() || password.getText().toString().isEmpty()) return;
            setLoading(true);
            app().api().login(email.getText().toString().trim(), password.getText().toString(), new ApiClient.Result<LoginResponse>() {
                @Override public void success(LoginResponse value) {
                    app().session().setLogin(value);
                    setLoading(false);
                    startActivity(new Intent(LoginActivity.this, ChatActivity.class));
                }
                @Override public void failure(Exception failure) {
                    setLoading(false);
                    error.setText(failure instanceof com.android.volley.VolleyError ? ApiError.message((com.android.volley.VolleyError) failure) : failure.getMessage());
                }
            });
        });
    }

    @Override protected void onResume() {
        super.onResume();
        if (app().session().getLogin() != null) startActivity(new Intent(this, ChatActivity.class));
    }

    private void setLoading(boolean loading) {
        signIn.setEnabled(!loading);
        progress.setVisibility(loading ? View.VISIBLE : View.GONE);
        if (loading) error.setText("");
    }
    private InfraAdvisorApplication app() { return (InfraAdvisorApplication) getApplication(); }
}
