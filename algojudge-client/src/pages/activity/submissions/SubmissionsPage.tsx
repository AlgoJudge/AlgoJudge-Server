import { Title } from "@mantine/core";
import classes from "./SubmissionsPage.module.css"
import { useTranslation } from "react-i18next";

interface Model {
    /* ... */
}

const data: Model = { /* ... */};

export default function SubmissionsPage() {
    const { t } = useTranslation();
    return (
        <>
            <Title>{t("My submissions")}</Title>
        </>
    );
}
