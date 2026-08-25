package dev.kyletaylor.infraadvisor.mobile.model;

public final class User {
    public final String id;
    public final String email;
    public final boolean isAdmin;
    public final boolean isServiceAccount;
    public final String createdAt;

    public User(String id, String email, boolean isAdmin, boolean isServiceAccount, String createdAt) {
        this.id = id;
        this.email = email;
        this.isAdmin = isAdmin;
        this.isServiceAccount = isServiceAccount;
        this.createdAt = createdAt;
    }
}
