import { Title } from "@mantine/core";
import classes from "./SubmitPage.module.css"
import { useTranslation } from "react-i18next";

interface Model {
    /* ... */
}

const data: Model = { /* ... */};

export default function SubmitPage() {
    const { t } = useTranslation();
    return (
        <>
            <Title>{t("Submit")}</Title>
        </>
    );
}
